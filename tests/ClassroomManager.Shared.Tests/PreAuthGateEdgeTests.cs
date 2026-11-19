// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>
/// The gate before authentication at its edges: how fast a source earns connections back, what it forgets when its table is full, and the
/// exact moments a wait ends. Found by mutation testing (Stryker, 2026-10-24): these could be changed without any test noticing.
/// </summary>
public sealed class PreAuthGateEdgeTests
{
    sealed class TestClock : TimeProvider
    {
        DateTimeOffset _now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Enters and leaves at once, so asking does not hold a slot.</summary>
    static Admission Ask(PreAuthGate gate, string source)
    {
        var result = gate.TryEnter(source);
        if (result == Admission.Admitted) gate.Leave(source);
        return result;
    }

    static void Block(PreAuthGate gate, string source)
    {
        for (var i = 0; i <= gate.Limits.FailuresBeforeBackoff; i++) gate.Failed(source);
        Assert.Equal(Admission.BackedOff, Ask(gate, source));
    }

    [Fact]
    public void A_source_earns_connections_back_at_the_refill_rate_and_never_more_than_its_burst()
    {
        var clock = new TestClock();
        var gate = new PreAuthGate(new ConnectionLimits { BurstPerSource = 2, RefillPerSecond = 4 }, clock);

        Assert.Equal(Admission.Admitted, Ask(gate, "10.0.0.1"));
        Assert.Equal(Admission.Admitted, Ask(gate, "10.0.0.1"));
        Assert.Equal(Admission.RateLimited, Ask(gate, "10.0.0.1"));

        clock.Advance(TimeSpan.FromMilliseconds(250));            // 4 per second: a quarter of a second earns exactly one
        Assert.Equal(Admission.Admitted, Ask(gate, "10.0.0.1"));
        Assert.Equal(Admission.RateLimited, Ask(gate, "10.0.0.1"));

        clock.Advance(TimeSpan.FromSeconds(10));                  // a long pause earns the burst, not forty
        Assert.Equal(Admission.Admitted, Ask(gate, "10.0.0.1"));
        Assert.Equal(Admission.Admitted, Ask(gate, "10.0.0.1"));
        Assert.Equal(Admission.RateLimited, Ask(gate, "10.0.0.1"));
    }

    [Fact]
    public void A_wait_ends_at_its_exact_end_and_not_a_moment_before()
    {
        var clock = new TestClock();
        var gate = new PreAuthGate(new ConnectionLimits { FailuresBeforeBackoff = 1, FirstBackoff = TimeSpan.FromSeconds(2) }, clock);
        gate.Failed("10.0.0.1");
        gate.Failed("10.0.0.1");                                  // the second failure is one more than allowed: wait 2 seconds

        clock.Advance(TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(1));
        Assert.Equal(Admission.BackedOff, Ask(gate, "10.0.0.1"));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(Admission.Admitted, Ask(gate, "10.0.0.1"));
    }

    [Fact]
    public void Failures_are_remembered_for_exactly_the_failure_memory_and_forgotten_after_it()
    {
        var clock = new TestClock();
        var limits = new ConnectionLimits { FailuresBeforeBackoff = 1, FirstBackoff = TimeSpan.FromSeconds(1), FailureMemory = TimeSpan.FromMinutes(10) };

        var remembers = new PreAuthGate(limits, clock);
        remembers.Failed("10.0.0.1");
        clock.Advance(TimeSpan.FromMinutes(10));                  // exactly the memory: still counted
        remembers.Failed("10.0.0.1");
        Assert.Equal(Admission.BackedOff, Ask(remembers, "10.0.0.1"));

        var forgets = new PreAuthGate(limits, clock);
        forgets.Failed("10.0.0.2");
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1));
        forgets.Failed("10.0.0.2");                               // the first one is forgotten, so this is the first again
        Assert.Equal(Admission.Admitted, Ask(forgets, "10.0.0.2"));
    }

    [Fact]
    public void Leaving_more_often_than_entering_never_makes_room_for_extra_connections()
    {
        var gate = new PreAuthGate(new ConnectionLimits { MaxPendingPerSource = 2, MaxPending = 3 }, new TestClock());
        gate.Leave("10.0.0.1");                                   // never entered
        Assert.Equal(Admission.Admitted, gate.TryEnter("10.0.0.1"));
        gate.Leave("10.0.0.1");
        gate.Leave("10.0.0.1");                                   // once too often
        Assert.Equal(0, gate.Pending);

        Assert.Equal(Admission.Admitted, gate.TryEnter("10.0.0.1"));
        Assert.Equal(Admission.Admitted, gate.TryEnter("10.0.0.1"));
        Assert.Equal(Admission.TooManyFromSource, gate.TryEnter("10.0.0.1"));   // still two, not three
        Assert.Equal(Admission.Admitted, gate.TryEnter("10.0.0.2"));
        Assert.Equal(Admission.TooManyPending, gate.TryEnter("10.0.0.3"));      // still three in all
    }

    [Fact]
    public void A_full_table_forgets_idle_sources_to_remember_a_new_one()
    {
        var clock = new TestClock();
        var gate = new PreAuthGate(new ConnectionLimits { MaxSources = 2, FailuresBeforeBackoff = 1 }, clock);
        Ask(gate, "10.0.0.1");
        Ask(gate, "10.0.0.2");

        clock.Advance(TimeSpan.FromMinutes(2));                   // both idle for more than a minute
        Block(gate, "10.0.0.3");                                  // remembered, so its failures count and it waits
    }

    [Fact]
    public void A_full_table_of_recent_sources_keeps_them_and_judges_a_newcomer_by_the_global_limits_alone()
    {
        var clock = new TestClock();
        var gate = new PreAuthGate(new ConnectionLimits { MaxSources = 2, FailuresBeforeBackoff = 1 }, clock);
        Ask(gate, "10.0.0.1");
        Ask(gate, "10.0.0.2");

        clock.Advance(TimeSpan.FromSeconds(30));                  // seen less than a minute ago: kept
        for (var i = 0; i < 5; i++) gate.Failed("10.0.0.3");
        Assert.Equal(Admission.Admitted, Ask(gate, "10.0.0.3"));  // not remembered, so not made to wait (memory stays bounded)
    }

    [Fact]
    public void A_waiting_source_or_one_with_a_connection_open_is_never_forgotten_to_make_room()
    {
        var clock = new TestClock();
        var gate = new PreAuthGate(new ConnectionLimits { MaxSources = 2, FailuresBeforeBackoff = 1, FirstBackoff = TimeSpan.FromMinutes(5), MaxBackoff = TimeSpan.FromMinutes(5) }, clock);
        Block(gate, "10.0.0.1");                                  // waits five minutes
        Assert.Equal(Admission.Admitted, gate.TryEnter("10.0.0.2"));   // holds a connection in setup

        clock.Advance(TimeSpan.FromMinutes(2));
        Ask(gate, "10.0.0.3");                                    // a newcomer finds the table full

        Assert.Equal(Admission.BackedOff, Ask(gate, "10.0.0.1")); // still remembered and still waiting
        Assert.Equal(1, gate.Pending);
    }
}
