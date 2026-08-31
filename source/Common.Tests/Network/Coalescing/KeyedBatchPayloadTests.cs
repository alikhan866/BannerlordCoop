using Common.Messaging;
using Common.Network.Coalescing;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Common.Tests.Network.Coalescing;

/// <summary>
/// Covers <see cref="KeyedBatchPayload{TEntry}"/>, which collapses many per-entry updates into one
/// message per coalesce key.
/// </summary>
/// <remarks>
/// The behaviour that matters is the combination: entries must deduplicate like
/// <see cref="LatestWinsPayload"/> does (so a troop type hit fifty times in a tick reports once, with its
/// final value), while entries with different keys must all survive (so batching never silently drops a
/// party's scoreboard line). Getting either half wrong is invisible on the wire — the message still sends,
/// it just carries the wrong rows.
/// </remarks>
public class KeyedBatchPayloadTests
{
    private readonly struct Entry
    {
        public readonly string Id;
        public readonly int Count;

        public Entry(string id, int count)
        {
            Id = id;
            Count = count;
        }
    }

    private sealed class BatchMessage : IMessage
    {
        public Entry[] Entries { get; }

        public BatchMessage(IReadOnlyCollection<Entry> entries) => Entries = entries.ToArray();
    }

    private static KeyedBatchPayload<Entry> Payload(string key, int count) =>
        new KeyedBatchPayload<Entry>(key, new Entry(key, count), e => new BatchMessage(e));

    private static Entry[] EntriesOf(ICoalescedPayload payload) =>
        ((BatchMessage)payload.ToMessage()).Entries;

    [Fact]
    public void A_single_update_produces_a_batch_of_one()
    {
        var entries = EntriesOf(Payload("a", 1));

        Assert.Single(entries);
        Assert.Equal(1, entries[0].Count);
    }

    [Fact]
    public void Distinct_entry_keys_all_survive_the_merge()
    {
        ICoalescedPayload merged = Payload("a", 1)
            .Merge(Payload("b", 2))
            .Merge(Payload("c", 3));

        var entries = EntriesOf(merged);

        // The whole point of batching: three troop types cost one message, not three, and none is lost.
        Assert.Equal(3, entries.Length);
        Assert.Equal(new[] { 1, 2, 3 }, entries.Select(e => e.Count).OrderBy(c => c));
    }

    [Fact]
    public void The_same_entry_key_keeps_only_the_newest_value()
    {
        ICoalescedPayload merged = Payload("a", 1)
            .Merge(Payload("a", 2))
            .Merge(Payload("a", 7));

        var entries = EntriesOf(merged);

        Assert.Single(entries);
        Assert.Equal(7, entries[0].Count);
    }

    [Fact]
    public void A_repeated_key_does_not_crowd_out_other_entries()
    {
        ICoalescedPayload merged = Payload("a", 1)
            .Merge(Payload("b", 2))
            .Merge(Payload("a", 9));

        var entries = EntriesOf(merged).OrderBy(e => e.Id).ToArray();

        Assert.Equal(2, entries.Length);
        Assert.Equal(9, entries[0].Count);
        Assert.Equal(2, entries[1].Count);
    }

    [Fact]
    public void Merging_accumulates_into_the_pending_payload_rather_than_copying_it()
    {
        var pending = Payload("a", 1);

        var merged = pending.Merge(Payload("b", 2));

        // Deliberately in place: copying on every merge is O(n*k) per flush window, which would add
        // quadratic work to the server game thread. SendCoalescer serializes every merge under its lock and
        // removes a payload from the pending table before building its message, so nothing can merge into a
        // payload that is already being flushed. This test pins that choice so a well-meaning change back to
        // copy-on-merge has to argue with it.
        Assert.Same(pending, merged);
        Assert.Equal(2, EntriesOf(merged).Length);
    }

    [Fact]
    public void The_built_message_does_not_alias_the_payloads_mutable_entries()
    {
        var payload = Payload("a", 1);
        var message = (BatchMessage)payload.ToMessage();

        payload.Merge(Payload("b", 2));

        // The message was already handed to the network layer; later merges must not retroactively
        // change what it says.
        Assert.Single(message.Entries);
    }

    [Fact]
    public void Entry_keys_are_compared_by_exact_ordinal_text()
    {
        // Ids are game object ids; two that differ only by case are different objects.
        var merged = Payload("Party_A", 1).Merge(Payload("party_a", 2));

        Assert.Equal(2, EntriesOf(merged).Length);
    }

    [Fact]
    public void Merging_a_foreign_payload_type_is_rejected_rather_than_silently_dropped()
    {
        var payload = Payload("a", 1);

        // A coalesce key must use one strategy for its whole life; mixing them would quietly discard
        // either the batch or the other payload depending on merge order.
        Assert.Throws<ArgumentException>(() => payload.Merge(new LatestWinsPayload(new BatchMessage(new List<Entry>()))));
    }

    [Fact]
    public void A_null_entry_key_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new KeyedBatchPayload<Entry>(null!, new Entry("a", 1), e => new BatchMessage(e)));
    }

    [Fact]
    public void A_null_builder_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new KeyedBatchPayload<Entry>("a", new Entry("a", 1), null!));
    }

    [Fact]
    public void The_message_is_built_at_flush_time_not_at_enqueue_time()
    {
        int builds = 0;
        var payload = new KeyedBatchPayload<Entry>(
            "a", new Entry("a", 1), e => { builds++; return new BatchMessage(e); });

        var merged = payload.Merge(Payload("b", 2));
        Assert.Equal(0, builds);

        merged.ToMessage();
        Assert.Equal(1, builds);
    }
}
