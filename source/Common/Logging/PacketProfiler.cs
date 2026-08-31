using Common.PacketHandlers;
using Common.Util;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Common.Logging;

/// <summary>
/// Tallies outbound network packets and periodically dumps, per packet type, how many were sent
/// and how many bytes they totalled — to profile what dominates network traffic.
/// </summary>
/// <remarks>
/// Fed from the network send path (see <c>CoopNetworkBase.SendInternal</c>), so every recorded packet is
/// one actually sent over the wire, counted with its serialized byte size. A <see cref="MessagePacket"/>
/// is broken out by the message type it wraps (e.g. <c>MessagePacket:NetworkTroopRosterElementBatch</c>).
/// The accumulated stats are dumped on a fixed wall-clock interval. Only the server profiles traffic
/// (see <see cref="ModInformation.IsServer"/>) unless the instance opts in with
/// <c>profileOnClient</c>.
/// </remarks>
/// <remarks>
/// The client opt-in exists for the mission P2P mesh (<c>LiteNetP2PClient</c>), which is the ONLY carrier
/// of per-agent battle traffic — agent movement, spawns, deaths — and which the server never sees. With the
/// default server-only guard, that traffic is completely unmeasured on both sides, so a battle that stutters
/// gives a healthy server profile and no evidence at all. A mesh profiler is fed from the client, so it has
/// to be allowed to record there; the guard stays the default so the server pipe is not suddenly profiled on
/// every client too.
/// </remarks>
public sealed class PacketProfiler : IDisposable
{
    // A logger to dump the packet profile.
    private static readonly ILogger Logger = LogManager.GetLogger<PacketProfiler>();

    // A task to periodically dump the accumulated stats.
    private readonly Poller poller;

    private readonly ConcurrentDictionary<string, Stats> stats = new ConcurrentDictionary<string, Stats>();

    /// <summary>
    /// Optional provider of a one-line live-state summary (e.g. per-peer reliable-queue depth and ping)
    /// appended to each dump. Owned by the network layer, which is the only one that can see peers;
    /// the profiler itself stays free of any networking dependency.
    /// </summary>
    public Func<string> ExtraStatsProvider { get; set; }

    /// <summary>Prefixes each dump so several profilers in one process stay tellable apart. Empty for the
    /// server pipe, which keeps its original wording so existing log readers still match.</summary>
    private readonly string scopePrefix;

    /// <summary>When true this profiler records on a client too; see the class remarks.</summary>
    private readonly bool profileOnClient;

    /// <summary>
    /// Constructs a PacketProfiler.
    /// </summary>
    /// <param name="dumpInterval">How often to dump the accumulated stats to the log.</param>
    /// <param name="scope">Short name for the pipe being profiled (e.g. <c>"Mesh"</c>), or null for the
    /// server pipe. Rendered as a leading <c>[scope] </c> tag on every dump.</param>
    /// <param name="profileOnClient">Allows recording on a client. Only the mission mesh wants this.</param>
    public PacketProfiler(TimeSpan dumpInterval, string scope = null, bool profileOnClient = false)
    {
        scopePrefix = string.IsNullOrEmpty(scope) ? string.Empty : $"[{scope}] ";
        this.profileOnClient = profileOnClient;
        poller = new Poller(Poll, dumpInterval);
        poller.Start();
    }

    /// <summary>
    /// Records one packet sent over the network and its serialized size in bytes. No-op off the server
    /// unless this profiler opted in with <c>profileOnClient</c>.
    /// </summary>
    public void Record(IPacket packet, int byteSize)
    {
        // Only the server profiles network traffic, unless this profiler owns a client-side pipe.
        if (!profileOnClient && ModInformation.IsClient) return;

        var packetName = GetPacketName(packet);

        stats.AddOrUpdate(packetName, _ => new Stats(1, byteSize), (_, existing) => existing.Add(byteSize));
    }

    // Dumps the accumulated stats and clears them for the next window.
    private void Poll(TimeSpan dt)
    {
        if (stats.IsEmpty) return;

        // Drain the stats into a snapshot, clearing the dictionary for the next window.
        var snapshot = new Dictionary<string, Stats>(stats.Count);
        foreach (var packetName in stats.Keys)
        {
            if (stats.TryRemove(packetName, out var packetStats))
            {
                snapshot[packetName] = packetStats;
            }
        }

        // Order by bytes sent (largest first) and format each entry as a friendly line. A list is
        // rendered in order by Serilog (a Dictionary's key order is not preserved by the sinks).
        var ordered = snapshot
            .OrderByDescending(entry => entry.Value.BytesSent)
            .Select(entry => $"{entry.Key}: {entry.Value.PacketsSent} packets, {entry.Value.BytesSent:N0} bytes")
            .ToList();

        // Average outbound throughput over the window: total bytes sent divided by the elapsed seconds.
        var totalBytes = snapshot.Values.Sum(s => s.BytesSent);
        var seconds = dt.TotalSeconds;
        var bytesPerSecond = seconds > 0 ? totalBytes / seconds : 0;

        // "Packet profile over" is kept verbatim so readers written against the server dump match both.
        Logger.Information(
            "{ScopePrefix}Packet profile over {Seconds:0.#} seconds ({BytesPerSecond:N0} bytes/sec avg): {@PacketProfile}{ExtraStats}",
            scopePrefix, seconds, bytesPerSecond, ordered, GetExtraStats());
    }

    // Never let a faulty provider kill the dump; the profile itself is the primary payload.
    private string GetExtraStats()
    {
        try
        {
            var extra = ExtraStatsProvider?.Invoke();
            return string.IsNullOrEmpty(extra) ? string.Empty : $" | {extra}";
        }
        catch (Exception ex)
        {
            return $" | peer stats unavailable: {ex.GetType().Name}";
        }
    }

    /// <summary>
    /// Formatted names by (packet type, wrapped message type). The pair is invariant, so the string is
    /// built once instead of per packet.
    /// </summary>
    /// <remarks>
    /// A cache rather than a micro-optimisation: the mission mesh profiler calls this on the per-recipient
    /// send funnel, which is the highest-frequency path in a battle. Formatting a fresh name per packet
    /// would add steady GC pressure to the exact path being profiled for stutter, and a profiler that
    /// changes what it measures is worse than none.
    /// </remarks>
    private static readonly ConcurrentDictionary<(Type Packet, Type Message), string> PacketNames =
        new ConcurrentDictionary<(Type, Type), string>();

    internal static string GetPacketNameForTest(IPacket packet) => GetPacketName(packet);

    /// <summary>How many distinct packet names the current window has recorded. Test seam: the only other
    /// evidence a packet was recorded is the periodic log dump.</summary>
    internal int RecordedTypeCountForTest => stats.Count;

    private static string GetPacketName(IPacket packet)
    {
        var packetType = packet.GetType();

        // Break MessagePacket out by the message type it wraps so it is not one opaque bucket.
        // Pattern matching, not `as`: MessagePacket is a struct.
        if (packet is not MessagePacket messagePacket || messagePacket.MessageType == null)
            return packetType.Name;

        return PacketNames.GetOrAdd(
            (packetType, messagePacket.MessageType),
            key => $"{key.Packet.Name}:{GetFriendlyTypeName(key.Message)}");
    }

    private static string GetFriendlyTypeName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var name = type.Name;
        var tickIndex = name.IndexOf('`');
        if (tickIndex > 0)
            name = name.Substring(0, tickIndex);

        var genericArgs = type.GetGenericArguments();
        var argNames = new string[genericArgs.Length];

        for (int i = 0; i < genericArgs.Length; i++)
        {
            argNames[i] = GetFriendlyTypeName(genericArgs[i]);
        }

        return $"{name}<{string.Join(", ", argNames)}>";
    }

    /// <summary>
    /// Disposes of the PacketProfiler, stopping the periodic dump.
    /// </summary>
    public void Dispose()
    {
        poller.StopAndWait(TimeSpan.FromSeconds(5));
    }

    // Running per-type totals: how many packets were sent and their combined serialized byte size.
    private readonly struct Stats
    {
        public readonly long PacketsSent;
        public readonly long BytesSent;

        public Stats(long packetsSent, long bytesSent)
        {
            PacketsSent = packetsSent;
            BytesSent = bytesSent;
        }

        public Stats Add(int byteSize) => new Stats(PacketsSent + 1, BytesSent + byteSize);
    }
}
