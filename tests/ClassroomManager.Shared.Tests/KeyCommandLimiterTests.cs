// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class KeyCommandLimiterTests
{
    sealed class FakeClock : TimeProvider
    {
        DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void A_teacher_key_may_send_a_limited_command_only_so_often_per_minute_and_the_minute_slides()
    {
        var clock = new FakeClock();
        var limiter = new KeyCommandLimiter(time: clock);

        for (var i = 0; i < 3; i++) Assert.True(limiter.TryTake("key-a", OperatorRole.Teacher, Cmd.PowerAction));
        Assert.False(limiter.TryTake("key-a", OperatorRole.Teacher, Cmd.PowerAction));

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(limiter.TryTake("key-a", OperatorRole.Teacher, Cmd.PowerAction));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(limiter.TryTake("key-a", OperatorRole.Teacher, Cmd.PowerAction));
    }

    [Fact]
    public void Another_key_another_command_and_an_admin_key_are_not_affected()
    {
        var limiter = new KeyCommandLimiter(time: new FakeClock());
        for (var i = 0; i < 3; i++) limiter.TryTake("key-a", OperatorRole.Teacher, Cmd.PowerAction);

        Assert.False(limiter.TryTake("key-a", OperatorRole.Teacher, Cmd.PowerAction));
        Assert.True(limiter.TryTake("key-b", OperatorRole.Teacher, Cmd.PowerAction));
        Assert.True(limiter.TryTake("key-a", OperatorRole.Teacher, Cmd.Ping));
        Assert.All(Enumerable.Range(0, 100), _ => Assert.True(limiter.TryTake("key-a", OperatorRole.Admin, Cmd.PowerAction)));
    }

    [Fact]
    public void The_limits_can_be_raised_lowered_or_removed_by_setting()
    {
        var clock = new FakeClock();
        var limiter = new KeyCommandLimiter(new Dictionary<string, int> { [Cmd.PowerAction] = 1, [Cmd.MessageShow] = 0, [Cmd.LockClear] = 2 }, clock);

        Assert.True(limiter.TryTake("k", OperatorRole.Teacher, Cmd.PowerAction));
        Assert.False(limiter.TryTake("k", OperatorRole.Teacher, Cmd.PowerAction));
        Assert.All(Enumerable.Range(0, 200), _ => Assert.True(limiter.TryTake("k", OperatorRole.Teacher, Cmd.MessageShow)));   // 0 = no limit
        Assert.True(limiter.TryTake("k", OperatorRole.Teacher, Cmd.LockClear));
        Assert.True(limiter.TryTake("k", OperatorRole.Teacher, Cmd.LockClear));
        Assert.False(limiter.TryTake("k", OperatorRole.Teacher, Cmd.LockClear));                                              // a new limit for a command
    }

    [Fact]
    public void Every_default_limit_is_for_a_command_that_a_teacher_may_send_and_is_generous_for_a_lesson()
    {
        foreach (var (command, perMinute) in KeyCommandLimiter.DefaultPerMinute)
        {
            Assert.True(CommandAccess.IsAllowed(OperatorRole.Teacher, command), command);
            Assert.InRange(perMinute, 3, 60);
        }
    }
}
