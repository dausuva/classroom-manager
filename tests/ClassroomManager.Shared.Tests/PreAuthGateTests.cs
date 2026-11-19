// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class PreAuthGateTests
{
    sealed class FakeClock : TimeProvider
    {
        DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void Connections_in_setup_are_capped_per_source_and_all_together()
    {
        var gate = new PreAuthGate(new ConnectionLimits { MaxPendingPerSource = 2, MaxPending = 3 }, new FakeClock());

        Assert.Equal(Admission.Admitted, gate.TryEnter("a"));
        Assert.Equal(Admission.Admitted, gate.TryEnter("a"));
        Assert.Equal(Admission.TooManyFromSource, gate.TryEnter("a"));
        Assert.Equal(Admission.Admitted, gate.TryEnter("b"));
        Assert.Equal(Admission.TooManyPending, gate.TryEnter("c"));

        gate.Leave("a");
        Assert.Equal(Admission.Admitted, gate.TryEnter("c"));
        Assert.Equal(3, gate.Pending);
    }

    [Fact]
    public void A_source_that_opens_connections_too_fast_waits_and_earns_them_back()
    {
        var clock = new FakeClock();
        var gate = new PreAuthGate(new ConnectionLimits { BurstPerSource = 3, RefillPerSecond = 1, MaxPendingPerSource = 100, MaxPending = 100 }, clock);

        for (var i = 0; i < 3; i++) { Assert.Equal(Admission.Admitted, gate.TryEnter("a")); gate.Leave("a"); }
        Assert.Equal(Admission.RateLimited, gate.TryEnter("a"));
        Assert.Equal(Admission.Admitted, gate.TryEnter("b"));   // another source is not affected

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(Admission.Admitted, gate.TryEnter("a"));
        gate.Leave("a");
        Assert.Equal(Admission.Admitted, gate.TryEnter("a"));
        gate.Leave("a");
        Assert.Equal(Admission.RateLimited, gate.TryEnter("a"));
    }

    [Fact]
    public void Failed_handshakes_start_a_doubling_wait_that_a_success_ends()
    {
        var clock = new FakeClock();
        var gate = new PreAuthGate(new ConnectionLimits { FailuresBeforeBackoff = 2, FirstBackoff = TimeSpan.FromSeconds(2), MaxBackoff = TimeSpan.FromSeconds(10) }, clock);

        gate.Failed("a"); gate.Failed("a");                     // forgiven: two failures allowed
        Assert.Equal(Admission.Admitted, gate.TryEnter("a"));
        gate.Leave("a");

        gate.Failed("a");                                       // third: 2 s
        Assert.Equal(Admission.BackedOff, gate.TryEnter("a"));
        clock.Advance(TimeSpan.FromSeconds(2.1));
        Assert.Equal(Admission.Admitted, gate.TryEnter("a"));
        gate.Leave("a");

        gate.Failed("a");                                       // fourth: 4 s
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(Admission.BackedOff, gate.TryEnter("a"));
        clock.Advance(TimeSpan.FromSeconds(1.1));
        Assert.Equal(Admission.Admitted, gate.TryEnter("a"));
        gate.Leave("a");

        gate.Failed("a"); gate.Failed("a"); gate.Failed("a");   // would be 8, 16, 32 s: the cap is 10
        clock.Advance(TimeSpan.FromSeconds(10.1));
        Assert.Equal(Admission.Admitted, gate.TryEnter("a"));
        gate.Leave("a");

        gate.Failed("a");
        gate.Succeeded("a");
        Assert.Equal(Admission.Admitted, gate.TryEnter("a"));
        gate.Leave("a");
        gate.Failed("a");                                       // counting started again
        Assert.Equal(Admission.Admitted, gate.TryEnter("a"));
    }

    [Fact]
    public void Failures_are_forgotten_after_a_quiet_time_and_one_source_does_not_block_another()
    {
        var clock = new FakeClock();
        var gate = new PreAuthGate(new ConnectionLimits { FailuresBeforeBackoff = 1, FailureMemory = TimeSpan.FromMinutes(10) }, clock);

        gate.Failed("a");
        gate.Failed("a");
        Assert.Equal(Admission.BackedOff, gate.TryEnter("a"));
        Assert.Equal(Admission.Admitted, gate.TryEnter("b"));

        clock.Advance(TimeSpan.FromMinutes(11));
        gate.Failed("a");                                       // the old two are forgotten: this is the first again
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(Admission.Admitted, gate.TryEnter("a"));
    }

    [Fact]
    public void A_flood_of_sources_cannot_grow_the_table_past_its_limit_and_still_leaves_room_for_the_teacher()
    {
        var clock = new FakeClock();
        var gate = new PreAuthGate(new ConnectionLimits { MaxSources = 50, MaxPending = 1000, MaxPendingPerSource = 8 }, clock);

        for (var i = 0; i < 500; i++)
        {
            var source = "10.0.0." + i;
            if (gate.TryEnter(source) == Admission.Admitted) gate.Leave(source);
        }
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(Admission.Admitted, gate.TryEnter("teacher"));
        Assert.Equal(1, gate.Pending);
    }

    [Fact]
    public void Refusals_are_counted_by_reason_and_the_counts_start_again()
    {
        var gate = new PreAuthGate(new ConnectionLimits { MaxPendingPerSource = 1 }, new FakeClock());

        gate.TryEnter("a");
        gate.TryEnter("a");
        gate.TryEnter("a");

        var counts = gate.TakeCounts();
        Assert.Equal(2, counts[Admission.TooManyFromSource]);
        Assert.DoesNotContain(Admission.Admitted, counts.Keys);
        Assert.Empty(gate.TakeCounts());
    }

    [Fact]
    public void The_defaults_let_one_teacher_address_open_many_connections_to_one_computer_at_once()
    {
        var gate = new PreAuthGate(time: new FakeClock());

        // Each Client has its own gate and sees the teacher's address only: the Host, a Health run and a scan together
        // open a dozen connections within a second, four at a time in setup.
        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(Admission.Admitted, gate.TryEnter("teacher"));
            if (i % 4 == 3) for (var k = 0; k < 4; k++) gate.Leave("teacher");
        }
    }
}
