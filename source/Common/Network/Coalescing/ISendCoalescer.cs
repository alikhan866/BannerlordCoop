using LiteNetLib;

namespace Common.Network.Coalescing;

/// <summary>
/// Buffers per-change network sends and collapses many updates to the same <see cref="CoalesceKey"/>
/// into one merged send per server tick. A send path enqueues instead of calling
/// <see cref="INetwork"/>.SendAll directly; the server flushes the buffer once per tick.
/// </summary>
/// <remarks>
/// Ordering obligations the consumers rely on:
/// <list type="bullet">
/// <item>The flush must run on the same thread that sends object creates and destroys, so a coalesced
/// update never reorders ahead of its object's create or after its destroy on the reliable-ordered
/// channel.</item>
/// <item>Before an object's destroy is sent, its pending updates must be sent (see
/// <see cref="FlushInstance"/>) or dropped (see <see cref="DropInstance"/>).</item>
/// </list>
/// </remarks>
public interface ISendCoalescer
{
    /// <summary>True when at least one update is buffered and awaiting a flush.</summary>
    bool HasPending { get; }

    /// <summary>
    /// Buffers an update for <paramref name="key"/>, merging it into any update already pending for that
    /// key via the payload's strategy.
    /// </summary>
    /// <summary>
    /// Limits a channel to at most one flush per <paramref name="minInterval"/>, letting its updates keep
    /// coalescing in between. Channels that never call this flush on every poll, as before.
    /// </summary>
    /// <remarks>
    /// Coalescing cannot beat the flush rate on its own: flushes are driven by the 25ms network poll, so a
    /// channel emits up to 40 messages a second however well each merges. The reliable queue is bounded by
    /// PACKET count, not bytes - it reached 12,926 entries at only 11 KB/s before a player was dropped - so
    /// the message rate itself is what has to come down. Opt-in, because only the caller knows whether its
    /// data can arrive a fraction of a second late.
    /// </remarks>
    void SetChannelInterval(string channel, System.TimeSpan minInterval);

    void Enqueue(CoalesceKey key, ICoalescedPayload payload);

    /// <summary>
    /// Buffers an update that will be sent only to <paramref name="peer"/> when flushed.
    /// </summary>
    void EnqueueToPeer(CoalesceKey key, ICoalescedPayload payload, NetPeer peer);

    /// <summary>
    /// Buffers an update that will be sent to every peer except <paramref name="excludedPeer"/> when flushed.
    /// </summary>
    void EnqueueToAllBut(CoalesceKey key, ICoalescedPayload payload, NetPeer excludedPeer);

    /// <summary>
    /// Sends the merged message for every pending key through its buffered delivery route and clears the
    /// buffer. Call once per server tick, on the thread that sends object creates and destroys.
    /// </summary>
    void Flush(INetwork network);

    /// <summary>
    /// Sends and clears the pending updates for one instance through their buffered delivery routes. Call
    /// before sending that instance's destroy so its final state reaches clients ahead of the destroy.
    /// </summary>
    void FlushInstance(string instanceId, INetwork network);

    /// <summary>Discards the pending updates for one instance without sending them.</summary>
    void DropInstance(string instanceId);
}
