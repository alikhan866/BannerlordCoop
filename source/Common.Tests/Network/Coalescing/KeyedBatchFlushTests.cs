using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Moq;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Common.Tests.Network.Coalescing;

/// <summary>
/// Proves the message-count reduction that <see cref="KeyedBatchPayload{TEntry}"/> exists for, by driving
/// a real <see cref="SendCoalescer"/> and counting what reaches the network.
/// </summary>
/// <remarks>
/// <para>
/// The reduction is the whole point, and it is not visible from the payload's own unit tests: a payload can
/// merge perfectly and still cost one message per key, because <see cref="SendCoalescer.Flush"/> sends each
/// pending key separately. These tests pin the end-to-end count.
/// </para>
/// <para>
/// Why message count and not bytes: the reliable send queue is measured in packets, and on a live siege it
/// grew 1,831 to 12,926 entries while throughput sat at 11 KB/sec, until the peer was declared overloaded
/// and dropped. Bandwidth was never the constraint — the number of discrete messages was.
/// </para>
/// </remarks>
public class KeyedBatchFlushTests
{
    private sealed class Entry
    {
        public string Key { get; init; } = "";
        public int Value { get; init; }
    }

    private sealed class BatchMessage : IMessage
    {
        public string Parent { get; }
        public Entry[] Entries { get; }

        public BatchMessage(string parent, IReadOnlyCollection<Entry> entries)
        {
            Parent = parent;
            Entries = entries.ToArray();
        }
    }

    private sealed class SingleMessage : IMessage
    {
        public string Parent { get; }
        public string Key { get; }

        public SingleMessage(string parent, string key)
        {
            Parent = parent;
            Key = key;
        }
    }

    private static (SendCoalescer coalescer, INetwork network, List<IMessage> sent) NewFixture()
    {
        var sent = new List<IMessage>();
        var network = new Mock<INetwork>();
        network.Setup(n => n.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(sent.Add);
        return (new SendCoalescer(), network.Object, sent);
    }

    private const string Channel = "scoreboard";
    private const string Battle = "MapEvent_1";

    [Fact]
    public void Keying_per_entry_costs_one_message_per_entry_which_is_the_behaviour_being_replaced()
    {
        var (coalescer, network, sent) = NewFixture();

        // The old shape: the coalesce key was the fine-grained (party + troop type) pair.
        for (int i = 0; i < 40; i++)
        {
            coalescer.Enqueue(
                new CoalesceKey(Channel, $"party{i}"),
                new LatestWinsPayload(new SingleMessage(Battle, $"party{i}")));
        }

        coalescer.Flush(network);

        // Forty pairs, forty messages — each repeating the battle id, each taking a reliable-queue slot.
        Assert.Equal(40, sent.Count);
    }

    [Fact]
    public void Keying_per_battle_costs_one_message_however_many_entries_changed()
    {
        var (coalescer, network, sent) = NewFixture();

        for (int i = 0; i < 40; i++)
        {
            coalescer.Enqueue(
                new CoalesceKey(Channel, Battle),
                new KeyedBatchPayload<Entry>(
                    $"party{i}",
                    new Entry { Key = $"party{i}", Value = i },
                    entries => new BatchMessage(Battle, entries)));
        }

        coalescer.Flush(network);

        // Forty down to one, with every entry preserved.
        var message = Assert.IsType<BatchMessage>(Assert.Single(sent));
        Assert.Equal(40, message.Entries.Length);
        Assert.Equal(Battle, message.Parent);
        Assert.Equal(Enumerable.Range(0, 40), message.Entries.Select(e => e.Value).OrderBy(v => v));
    }

    [Fact]
    public void Repeated_updates_to_one_entry_still_cost_one_message_carrying_the_final_value()
    {
        var (coalescer, network, sent) = NewFixture();

        for (int i = 0; i < 50; i++)
        {
            coalescer.Enqueue(
                new CoalesceKey(Channel, Battle),
                new KeyedBatchPayload<Entry>(
                    "party0",
                    new Entry { Key = "party0", Value = i },
                    entries => new BatchMessage(Battle, entries)));
        }

        coalescer.Flush(network);

        var message = Assert.IsType<BatchMessage>(Assert.Single(sent));
        var entry = Assert.Single(message.Entries);
        Assert.Equal(49, entry.Value);
    }

    [Fact]
    public void Separate_battles_still_get_separate_messages()
    {
        var (coalescer, network, sent) = NewFixture();

        foreach (var battle in new[] { "MapEvent_1", "MapEvent_2" })
        {
            for (int i = 0; i < 10; i++)
            {
                var captured = battle;
                coalescer.Enqueue(
                    new CoalesceKey(Channel, captured),
                    new KeyedBatchPayload<Entry>(
                        $"party{i}",
                        new Entry { Key = $"party{i}", Value = i },
                        entries => new BatchMessage(captured, entries)));
            }
        }

        coalescer.Flush(network);

        // Batching must not merge two concurrent battles into one message addressed to the wrong event.
        Assert.Equal(2, sent.Count);
        Assert.Equal(
            new[] { "MapEvent_1", "MapEvent_2" },
            sent.Cast<BatchMessage>().Select(m => m.Parent).OrderBy(p => p));
        Assert.All(sent.Cast<BatchMessage>(), m => Assert.Equal(10, m.Entries.Length));
    }

    [Fact]
    public void A_second_flush_after_no_new_updates_sends_nothing()
    {
        var (coalescer, network, sent) = NewFixture();

        coalescer.Enqueue(
            new CoalesceKey(Channel, Battle),
            new KeyedBatchPayload<Entry>(
                "party0",
                new Entry { Key = "party0", Value = 1 },
                entries => new BatchMessage(Battle, entries)));

        coalescer.Flush(network);
        coalescer.Flush(network);

        // A batch left pending after its flush would re-send stale rows every tick forever.
        Assert.Single(sent);
    }

    [Fact]
    public void Updates_arriving_after_a_flush_start_a_fresh_batch()
    {
        var (coalescer, network, sent) = NewFixture();

        coalescer.Enqueue(
            new CoalesceKey(Channel, Battle),
            new KeyedBatchPayload<Entry>(
                "party0", new Entry { Key = "party0", Value = 1 },
                entries => new BatchMessage(Battle, entries)));
        coalescer.Flush(network);

        coalescer.Enqueue(
            new CoalesceKey(Channel, Battle),
            new KeyedBatchPayload<Entry>(
                "party1", new Entry { Key = "party1", Value = 2 },
                entries => new BatchMessage(Battle, entries)));
        coalescer.Flush(network);

        Assert.Equal(2, sent.Count);
        // The second batch carries only what changed since the first, not a growing accumulation.
        Assert.Single(((BatchMessage)sent[1]).Entries);
        Assert.Equal("party1", ((BatchMessage)sent[1]).Entries[0].Key);
    }
}
