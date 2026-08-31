using System;
using System.Collections.Generic;
using LiteNetLib;

namespace Common.Network.Coalescing;

/// <inheritdoc cref="ISendCoalescer"/>
public sealed class SendCoalescer : ISendCoalescer
{
    private enum DeliveryMode
    {
        Broadcast,
        Peer,
        AllButPeer,
    }

    private sealed class PendingSend
    {
        public ICoalescedPayload Payload { get; set; }
        public DeliveryMode Mode { get; }
        public NetPeer Peer { get; }

        public PendingSend(ICoalescedPayload payload, DeliveryMode mode, NetPeer peer)
        {
            Payload = payload;
            Mode = mode;
            Peer = peer;
        }

        public void Send(INetwork network)
        {
            var message = Payload.ToMessage();
            switch (Mode)
            {
                case DeliveryMode.Broadcast:
                    network.SendAll(message);
                    break;
                case DeliveryMode.Peer:
                    network.Send(Peer, message);
                    break;
                case DeliveryMode.AllButPeer:
                    network.SendAllBut(Peer, message);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown coalesced delivery mode {Mode}.");
            }
        }
    }

    private readonly Dictionary<CoalesceKey, PendingSend> pending = new();
    private readonly List<CoalesceKey> order = new();
    private readonly object gate = new();

    /// <summary>Minimum gap between flushes for a channel that asked for one. Absent means every flush.</summary>
    private readonly Dictionary<string, TimeSpan> channelIntervals = new();
    private readonly Dictionary<string, DateTime> channelLastFlushUtc = new();
    private readonly List<CoalesceKey> retained = new();

    /// <summary>
    /// Holds a channel's sends back to at most one flush per <paramref name="minInterval"/>, letting its
    /// updates keep coalescing in between.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Coalescing alone cannot beat the flush rate. <c>Flush</c> is driven by the network poll every 25ms,
    /// so a channel can emit 40 messages a second no matter how well each one merges — measured on a live
    /// siege, scoreboard updates cost ~700 messages per 10 seconds across ~400 flushes, barely two merged
    /// entries each. The binding constraint is the reliable queue's PACKET count (it reached 12,926 with
    /// throughput at only 11 KB/s before the peer was dropped), so what has to come down is the number of
    /// messages, not their size.
    /// </para>
    /// <para>
    /// Opt-in per channel, because the right interval is a gameplay judgement, not a transport one: a
    /// scoreboard or an xp bar can lag a fraction of a second unnoticed, whereas a barter or a mission
    /// handshake cannot. Channels that never call this keep flushing on every poll exactly as before.
    /// Instance-scoped flushes (<see cref="FlushInstance"/>) ignore the throttle — they exist precisely to
    /// force pending state out ahead of dependent traffic.
    /// </para>
    /// </remarks>
    public void SetChannelInterval(string channel, TimeSpan minInterval)
    {
        if (string.IsNullOrEmpty(channel)) throw new ArgumentNullException(nameof(channel));

        lock (gate)
        {
            if (minInterval <= TimeSpan.Zero) channelIntervals.Remove(channel);
            else channelIntervals[channel] = minInterval;
        }
    }

    public bool HasPending
    {
        get
        {
            lock (gate)
            {
                return pending.Count > 0;
            }
        }
    }

    public void Enqueue(CoalesceKey key, ICoalescedPayload payload)
    {
        Enqueue(key, payload, DeliveryMode.Broadcast, null);
    }

    public void EnqueueToPeer(CoalesceKey key, ICoalescedPayload payload, NetPeer peer)
    {
        if (peer == null) throw new ArgumentNullException(nameof(peer));
        Enqueue(key, payload, DeliveryMode.Peer, peer);
    }

    public void EnqueueToAllBut(CoalesceKey key, ICoalescedPayload payload, NetPeer excludedPeer)
    {
        if (excludedPeer == null) throw new ArgumentNullException(nameof(excludedPeer));
        Enqueue(key, payload, DeliveryMode.AllButPeer, excludedPeer);
    }

    private void Enqueue(CoalesceKey key, ICoalescedPayload payload, DeliveryMode mode, NetPeer peer)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));

        lock (gate)
        {
            if (pending.TryGetValue(key, out var existing))
            {
                if (existing.Mode != mode || !ReferenceEquals(existing.Peer, peer))
                {
                    throw new InvalidOperationException(
                        $"Coalesce key {key} cannot reuse a different delivery route.");
                }

                existing.Payload = existing.Payload.Merge(payload);
                return;
            }

            pending.Add(key, new PendingSend(payload, mode, peer));
            order.Add(key);
        }
    }

    public void Flush(INetwork network) => Flush(network, DateTime.UtcNow);

    /// <summary>Flush at an explicit instant. Test seam for the per-channel interval.</summary>
    internal void Flush(INetwork network, DateTime nowUtc)
    {
        if (network == null) throw new ArgumentNullException(nameof(network));

        PendingSend[] toSend;
        lock (gate)
        {
            if (pending.Count == 0) return;

            var due = new List<PendingSend>(pending.Count);
            retained.Clear();

            foreach (var key in order)
            {
                if (!IsChannelDue(key.Channel, nowUtc))
                {
                    // Held back deliberately: its updates keep merging into the pending payload, so the
                    // next flush carries more entries in the same single message.
                    retained.Add(key);
                    continue;
                }

                due.Add(pending[key]);
                pending.Remove(key);
                channelLastFlushUtc[key.Channel] = nowUtc;
            }

            if (due.Count == 0) return;

            order.Clear();
            order.AddRange(retained);
            retained.Clear();

            toSend = due.ToArray();
        }

        foreach (var pendingSend in toSend)
        {
            pendingSend.Send(network);
        }
    }

    // Caller holds the gate.
    private bool IsChannelDue(string channel, DateTime nowUtc)
    {
        if (channel == null || !channelIntervals.TryGetValue(channel, out var interval))
            return true;

        if (!channelLastFlushUtc.TryGetValue(channel, out var last))
            return true;

        var elapsed = nowUtc - last;
        // A clock that stepped backwards must not wedge a channel shut until real time catches up.
        return elapsed >= interval || elapsed < TimeSpan.Zero;
    }

    public void FlushInstance(string instanceId, INetwork network)
    {
        if (network == null) throw new ArgumentNullException(nameof(network));

        List<PendingSend> toSend = ExtractInstance(instanceId);
        if (toSend == null) return;

        foreach (var pendingSend in toSend)
        {
            pendingSend.Send(network);
        }
    }

    public void DropInstance(string instanceId)
    {
        ExtractInstance(instanceId);
    }

    // Removes and returns every pending payload for the instance, or null if none. The caller decides
    // whether to send them (FlushInstance) or discard them (DropInstance).
    private List<PendingSend> ExtractInstance(string instanceId)
    {
        lock (gate)
        {
            List<PendingSend> payloads = null;
            for (int i = 0; i < order.Count;)
            {
                var key = order[i];
                if (string.Equals(key.InstanceId, instanceId, StringComparison.Ordinal))
                {
                    (payloads ??= new List<PendingSend>()).Add(pending[key]);
                    pending.Remove(key);
                    order.RemoveAt(i);
                    continue;
                }

                i++;
            }

            return payloads;
        }
    }
}
