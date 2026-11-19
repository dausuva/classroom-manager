// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class SessionLimitsTests
{
    sealed class FakeClock : TimeProvider
    {
        DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void A_connection_may_burst_then_is_held_to_the_rate_and_earns_requests_back()
    {
        var clock = new FakeClock();
        var bucket = new RequestBucket(burst: 5, perSecond: 10, clock);

        for (var i = 0; i < 5; i++) Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());

        clock.Advance(TimeSpan.FromSeconds(0.25));      // 2.5 requests earned
        Assert.True(bucket.TryTake());
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());

        clock.Advance(TimeSpan.FromMinutes(5));         // never more than the burst
        for (var i = 0; i < 5; i++) Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());
    }

    [Fact]
    public void Sessions_are_counted_per_key_and_slots_come_back()
    {
        var sessions = new SessionCounter();

        Assert.True(sessions.TryOpen("k1", 2));
        Assert.True(sessions.TryOpen("k1", 2));
        Assert.False(sessions.TryOpen("k1", 2));
        Assert.True(sessions.TryOpen("k2", 2));

        sessions.Close("k1");
        Assert.Equal(1, sessions.Open("k1"));
        Assert.True(sessions.TryOpen("k1", 2));

        sessions.Close("k1"); sessions.Close("k1"); sessions.Close("k1");   // closing too often does not go below zero
        Assert.Equal(0, sessions.Open("k1"));
        Assert.Equal(1, sessions.Open("k2"));
    }
}
