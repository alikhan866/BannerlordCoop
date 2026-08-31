using Common.Messaging;
using Common.Network;
using Common.Network.Coalescing;
using Moq;
using System;
using System.Collections.Generic;
using Xunit;

namespace Common.Tests.Network.Coalescing;

/// <summary>
/// Covers the per-channel flush interval, which caps how many messages a channel can put on the wire.
/// </summary>
/// <remarks>
/// <para>
/// Coalescing merges updates but cannot beat the flush rate: <c>Flush</c> runs on the 25ms network poll, so
/// a busy channel emitted up to 40 messages a second no matter how well each merged. Measured on a live
/// siege, scoreboard traffic cost ~700 messages per 10 seconds spread across ~400 flushes — barely two
/// merged entries per message. Meanwhile the reliable queue, which is bounded by PACKET count rather than
/// bytes, climbed to 12,926 entries at only 11 KB/s and the peer was dropped. Cutting the flush rate is
/// therefore the lever that coalescing alone could not reach.
/// </para>
/// <para>
/// The risk this pins is the opposite one: a throttle that leaked onto channels which never asked for it
/// would delay barter, mission handshakes and other traffic where a late message is a bug.
/// </para>
/// </remarks>
public class SendCoalescerChannelIntervalTests
{
    private sealed class Probe : IMessage
    {
        public int Value { get; }
        public Probe(int value) => Value = value;
    }

    private static (SendCoalescer coalescer, INetwork network, List<IMessage> sent) NewFixture()
    {
        var sent = new List<IMessage>();
        var network = new Mock<INetwork>();
        network.Setup(n => n.SendAll(It.IsAny<IMessage>())).Callback<IMessage>(sent.Add);
        return (new SendCoalescer(), network.Object, sent);
    }

    private static readonly DateTime T0 = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);
    private const string Throttled = "scoreboard";
    private const string Plain = "barter";

    private static void Enqueue(SendCoalescer coalescer, string channel, int value) =>
        coalescer.Enqueue(new CoalesceKey(channel, "instance"), new LatestWinsPayload(new Probe(value)));

    [Fact]
    public void An_unconfigured_channel_flushes_on_every_poll_exactly_as_before()
    {
        var (coalescer, network, sent) = NewFixture();

        for (int i = 0; i < 5; i++)
        {
            Enqueue(coalescer, Plain, i);
            coalescer.Flush(network, T0 + TimeSpan.FromMilliseconds(25 * i));
        }

        // Barter, mission handshakes and the rest must not inherit a delay they never asked for.
        Assert.Equal(5, sent.Count);
    }

    [Fact]
    public void A_throttled_channel_sends_once_per_interval_however_often_the_poll_runs()
    {
        var (coalescer, network, sent) = NewFixture();
        coalescer.SetChannelInterval(Throttled, Interval);

        // 400ms of 25ms polls: 16 flushes, an update waiting at every one.
        for (int i = 0; i < 16; i++)
        {
            Enqueue(coalescer, Throttled, i);
            coalescer.Flush(network, T0 + TimeSpan.FromMilliseconds(25 * i));
        }

        // Sends at t=0 (which establishes the channel's clock) and again at t=200. The other 14 polls
        // are held: 16 opportunities to send, reduced to 2.
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public void Updates_held_back_by_the_throttle_are_delivered_not_dropped()
    {
        var (coalescer, network, sent) = NewFixture();
        coalescer.SetChannelInterval(Throttled, Interval);

        Enqueue(coalescer, Throttled, 1);
        coalescer.Flush(network, T0);
        Assert.Single(sent);

        Enqueue(coalescer, Throttled, 2);
        coalescer.Flush(network, T0 + TimeSpan.FromMilliseconds(25));
        Assert.Single(sent);

        coalescer.Flush(network, T0 + Interval);

        // Held, then sent — never discarded.
        Assert.Equal(2, sent.Count);
        Assert.Equal(2, Assert.IsType<Probe>(sent[1]).Value);
    }

    [Fact]
    public void A_held_channel_keeps_coalescing_so_the_delayed_message_carries_the_latest_value()
    {
        var (coalescer, network, sent) = NewFixture();
        coalescer.SetChannelInterval(Throttled, Interval);

        Enqueue(coalescer, Throttled, 0);
        coalescer.Flush(network, T0);

        // Strictly inside the window: 25ms..175ms, so none of these polls may send.
        for (int i = 1; i <= 7; i++)
        {
            Enqueue(coalescer, Throttled, i);
            coalescer.Flush(network, T0 + TimeSpan.FromMilliseconds(25 * i));
        }

        Assert.Single(sent);

        coalescer.Flush(network, T0 + Interval);

        // Seven updates collapsed into the one message the throttle allowed, carrying the newest value.
        Assert.Equal(2, sent.Count);
        Assert.Equal(7, Assert.IsType<Probe>(sent[1]).Value);
    }

    [Fact]
    public void Throttling_one_channel_does_not_hold_up_another_in_the_same_flush()
    {
        var (coalescer, network, sent) = NewFixture();
        coalescer.SetChannelInterval(Throttled, Interval);

        Enqueue(coalescer, Throttled, 1);
        Enqueue(coalescer, Plain, 1);
        coalescer.Flush(network, T0);
        sent.Clear();

        Enqueue(coalescer, Throttled, 2);
        Enqueue(coalescer, Plain, 2);
        coalescer.Flush(network, T0 + TimeSpan.FromMilliseconds(25));

        // Only the unthrottled one goes; a shared flush must not become a shared delay.
        Assert.Single(sent);
        Assert.Equal(2, Assert.IsType<Probe>(sent[0]).Value);
    }

    [Fact]
    public void Clearing_the_interval_restores_flush_on_every_poll()
    {
        var (coalescer, network, sent) = NewFixture();
        coalescer.SetChannelInterval(Throttled, Interval);
        Enqueue(coalescer, Throttled, 1);
        coalescer.Flush(network, T0);
        sent.Clear();

        coalescer.SetChannelInterval(Throttled, TimeSpan.Zero);

        Enqueue(coalescer, Throttled, 2);
        coalescer.Flush(network, T0 + TimeSpan.FromMilliseconds(25));

        Assert.Single(sent);
    }

    [Fact]
    public void An_instance_flush_ignores_the_throttle()
    {
        var (coalescer, network, sent) = NewFixture();
        coalescer.SetChannelInterval(Throttled, Interval);
        Enqueue(coalescer, Throttled, 1);
        coalescer.Flush(network, T0);
        sent.Clear();

        Enqueue(coalescer, Throttled, 2);
        coalescer.FlushInstance("instance", network);

        // FlushInstance is a barrier: it exists to force pending state out ahead of dependent traffic,
        // so a rate limit must never make it a no-op.
        Assert.Single(sent);
    }

    [Fact]
    public void A_clock_that_jumps_backwards_does_not_wedge_a_channel_shut()
    {
        var (coalescer, network, sent) = NewFixture();
        coalescer.SetChannelInterval(Throttled, Interval);
        Enqueue(coalescer, Throttled, 1);
        coalescer.Flush(network, T0);
        sent.Clear();

        Enqueue(coalescer, Throttled, 2);
        coalescer.Flush(network, T0 - TimeSpan.FromMinutes(5));

        Assert.Single(sent);
    }

    [Fact]
    public void A_flush_where_every_channel_is_held_leaves_the_pending_work_intact()
    {
        var (coalescer, network, sent) = NewFixture();
        coalescer.SetChannelInterval(Throttled, Interval);
        Enqueue(coalescer, Throttled, 1);
        coalescer.Flush(network, T0);
        sent.Clear();

        Enqueue(coalescer, Throttled, 2);
        coalescer.Flush(network, T0 + TimeSpan.FromMilliseconds(25));

        Assert.Empty(sent);
        Assert.True(coalescer.HasPending);

        coalescer.Flush(network, T0 + Interval);
        Assert.Single(sent);
    }

    [Fact]
    public void A_null_channel_name_is_rejected()
    {
        var (coalescer, _, _) = NewFixture();

        Assert.Throws<ArgumentNullException>(() => coalescer.SetChannelInterval(null!, Interval));
        Assert.Throws<ArgumentNullException>(() => coalescer.SetChannelInterval("", Interval));
    }
}
