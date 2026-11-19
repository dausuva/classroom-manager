// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class DiscoveryLimiterTests
{
    sealed class FakeClock : TimeProvider
    {
        DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void A_thousand_probes_from_one_source_get_only_the_allowed_answers()
    {
        var clock = new FakeClock();
        var limiter = new DiscoveryLimiter(new DiscoveryLimits { PerSourcePerSecond = 2, PerSourcePerMinute = 20 }, clock);

        var answered = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (limiter.TryAnswer("10.0.0.9")) answered++;
            clock.Advance(TimeSpan.FromMilliseconds(10));      // 1,000 probes in 10 seconds
        }

        Assert.Equal(20, answered);                           // 2 a second would allow 20 in 10 s; the minute cap holds it there
        Assert.Equal(1000 - answered, limiter.TakeDropped());
        Assert.Equal(0, limiter.TakeDropped());
    }

    [Fact]
    public void The_per_second_cap_alone_spaces_the_answers_and_a_quiet_source_is_answered_again()
    {
        var clock = new FakeClock();
        var limiter = new DiscoveryLimiter(new DiscoveryLimits { PerSourcePerSecond = 2, PerSourcePerMinute = 1000 }, clock);

        Assert.True(limiter.TryAnswer("a"));
        Assert.True(limiter.TryAnswer("a"));
        Assert.False(limiter.TryAnswer("a"));
        Assert.True(limiter.TryAnswer("b"));                  // another source has its own allowance

        clock.Advance(TimeSpan.FromSeconds(1.01));
        Assert.True(limiter.TryAnswer("a"));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(limiter.TryAnswer("a"));
    }

    [Fact]
    public void All_sources_together_are_capped_and_the_table_cannot_grow_without_limit()
    {
        var clock = new FakeClock();
        var limiter = new DiscoveryLimiter(new DiscoveryLimits { GlobalPerSecond = 5 }, clock);
        Assert.Equal(5, Enumerable.Range(0, 50).Count(i => limiter.TryAnswer("10.0.0." + i)));

        var small = new DiscoveryLimiter(new DiscoveryLimits { MaxSources = 10, GlobalPerSecond = 1000 }, clock);
        Assert.Equal(10, Enumerable.Range(0, 50).Count(i => small.TryAnswer("10.0.0." + i)));   // a full table of busy sources answers no newcomer
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(small.TryAnswer("10.0.1.1"));                                               // once the old ones are idle there is room again
    }

    [Theory]
    [InlineData("10.42.4.5", "10.42.4.200", 24, true)]
    [InlineData("10.42.4.5", "10.42.5.5", 24, false)]
    [InlineData("10.42.4.5", "10.42.5.5", 23, true)]
    [InlineData("10.1.2.3", "192.168.0.1", 0, true)]
    [InlineData("10.1.2.3", "10.1.2.3", 32, true)]
    [InlineData("10.1.2.3", "10.1.2.4", 32, false)]
    [InlineData("10.1.2.3", "10.1.2.4", 33, false)]
    public void Subnet_membership_follows_the_prefix_length(string a, string b, int prefix, bool expected) =>
        Assert.Equal(expected, DiscoveryLimiter.SameSubnet(IPAddress.Parse(a), IPAddress.Parse(b), prefix));

    [Fact]
    public void An_address_written_as_ipv4_mapped_ipv6_is_read_as_ipv4_and_ipv6_never_matches()
    {
        Assert.True(DiscoveryLimiter.SameSubnet(IPAddress.Parse("::ffff:10.42.4.5"), IPAddress.Parse("10.42.4.9"), 24));
        Assert.False(DiscoveryLimiter.SameSubnet(IPAddress.Parse("fe80::1"), IPAddress.Parse("fe80::2"), 64));
    }

    [Fact]
    public void The_reply_is_not_many_times_the_question()
    {
        var question = System.Text.Encoding.UTF8.GetByteCount(Protocol.DiscoverMagic);
        var reply = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new DiscoveryReply
        {
            MachineName = "410-17", ClientId = "0123456789abcdef0123456789abcdef", Mac = "C46516BDAFE1", TcpPort = 5050,
        }, Protocol.Json);

        // Measured 2026-10-23: 21 bytes in, about 123 out (5.9 times). The ratio is recorded here so a growing reply is noticed.
        Assert.InRange((double)reply.Length / question, 1, 8);
    }
}
