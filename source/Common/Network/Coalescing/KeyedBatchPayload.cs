using Common.Messaging;
using System;
using System.Collections.Generic;

namespace Common.Network.Coalescing;

/// <summary>
/// Accumulates many per-entry updates under one coalesce key and emits them as a SINGLE batched message,
/// keeping only the latest value for each entry key.
/// </summary>
/// <remarks>
/// <para>
/// The gap this fills: <see cref="LatestWinsPayload"/> deduplicates one key, but <see cref="SendCoalescer"/>
/// still sends one message per key, so N distinct keys cost N messages however well each one is deduplicated.
/// A battle that touches hundreds of (party, troop-type) pairs therefore paid full message framing hundreds
/// of times per flush, and every one of those messages repeated the same parent id.
/// </para>
/// <para>
/// Use this where the natural coalesce key is coarse (the battle) but updates arrive per fine-grained entry
/// (each troop type in each party): key the coalescer by the coarse id, key the entries by the fine one, and
/// the whole flush becomes one message carrying the parent id once.
/// </para>
/// </remarks>
/// <typeparam name="TEntry">The per-entry payload carried in the batch.</typeparam>
public sealed class KeyedBatchPayload<TEntry> : ICoalescedPayload
{
    private readonly Dictionary<string, TEntry> entries;
    private readonly Func<IReadOnlyCollection<TEntry>, IMessage> build;

    /// <summary>Starts a batch holding one entry.</summary>
    /// <param name="entryKey">Identifies the entry within the batch; a later update with the same key
    /// replaces this one.</param>
    /// <param name="entry">The entry value.</param>
    /// <param name="build">Builds the wire message from the accumulated entries, at flush time.</param>
    public KeyedBatchPayload(string entryKey, TEntry entry, Func<IReadOnlyCollection<TEntry>, IMessage> build)
    {
        if (entryKey == null) throw new ArgumentNullException(nameof(entryKey));
        this.build = build ?? throw new ArgumentNullException(nameof(build));

        entries = new Dictionary<string, TEntry>(StringComparer.Ordinal) { [entryKey] = entry };
    }

    /// <summary>How many distinct entries this batch currently carries.</summary>
    public int Count => entries.Count;

    /// <summary>
    /// Folds <paramref name="incoming"/>'s entries into this batch IN PLACE and returns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In place, rather than returning a fresh copy, because copying is quadratic exactly where this type
    /// is meant to help. A flush window with n enqueues spread over k entries would copy the dictionary on
    /// every merge — O(n·k) insertions per flush on the server's game thread. Under battle load that is
    /// enough work to matter, and adding quadratic cost while removing quadratic traffic is no bargain.
    /// </para>
    /// <para>
    /// Safe because of <see cref="SendCoalescer"/>'s contract, which this type is only ever used through:
    /// every <c>Merge</c> runs under the coalescer's lock, and a flush clears the pending table under that
    /// same lock BEFORE calling <see cref="ToMessage"/> outside it. So once a payload has been taken for a
    /// flush, no further merge can reach it — a later enqueue for the same key starts a new payload.
    /// </para>
    /// </remarks>
    public ICoalescedPayload Merge(ICoalescedPayload incoming)
    {
        if (incoming is not KeyedBatchPayload<TEntry> other)
        {
            throw new ArgumentException(
                $"Cannot merge {incoming?.GetType().Name ?? "null"} into KeyedBatchPayload<{typeof(TEntry).Name}>; " +
                "a coalesce key must use a single payload type.",
                nameof(incoming));
        }

        foreach (var entry in other.entries)
        {
            // Newer wins, matching LatestWinsPayload's rule applied per entry.
            entries[entry.Key] = entry.Value;
        }

        return this;
    }

    /// <summary>Builds the batch message. The entries are copied out by the builder, so the message does
    /// not alias this payload's mutable state.</summary>
    public IMessage ToMessage() => build(entries.Values);
}
