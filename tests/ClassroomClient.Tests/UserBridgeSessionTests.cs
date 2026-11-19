// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Client;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>
/// Which signed-in accounts a request reaches (found on a real computer on 2026-11-04): a lock that only covers the account at the screen can be walked around by
/// switching to a second account that is already signed in, and an unlock sent while another account is at the screen leaves the first one locked.
/// </summary>
public sealed class UserBridgeSessionTests
{
    [Theory]
    [InlineData(Cmd.LockSet)]
    [InlineData(Cmd.LockClear)]
    public void A_lock_and_its_release_go_to_every_signed_in_account(string cmd)
    {
        Assert.Equal([1, 2], UserBridge.SessionsFor(cmd, [1, 2], console: 2));
        Assert.Equal([1, 2], UserBridge.SessionsFor(cmd, [1, 2], console: 1));
        Assert.Equal([3], UserBridge.SessionsFor(cmd, [3], console: 3));
    }

    [Theory]
    [InlineData(Cmd.MessageShow)]
    [InlineData(Cmd.MessageClear)]
    public void Other_requests_go_only_to_the_account_at_the_screen(string cmd)
    {
        Assert.Equal([2], UserBridge.SessionsFor(cmd, [1, 2], console: 2));
        Assert.Equal([1], UserBridge.SessionsFor(cmd, [1, 2], console: 1));
    }

    [Fact]
    public void When_no_signed_in_account_is_at_the_screen_the_last_connected_helper_is_used_and_with_none_nobody_is()
    {
        Assert.Equal([2], UserBridge.SessionsFor(Cmd.MessageShow, [1, 2], console: 7));
        Assert.Empty(UserBridge.SessionsFor(Cmd.LockSet, [], console: 1));
        Assert.Empty(UserBridge.SessionsFor(Cmd.MessageShow, [], console: 1));
    }
}

public sealed class LockTimeoutTests
{
    [Fact]
    public void A_lock_lasts_thirty_minutes_unless_the_host_asks_for_another_length()
    {
        Assert.Equal(30, LockPolicy.Minutes(new System.Text.Json.Nodes.JsonObject { ["title"] = "x", ["message"] = "y" }));
        Assert.Equal(30, LockPolicy.Minutes(null));
        Assert.Equal(45, LockPolicy.Minutes(new System.Text.Json.Nodes.JsonObject { ["minutes"] = 45 }));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100000, 720)]
    public void A_requested_length_is_kept_between_one_minute_and_twelve_hours(int asked, int expected)
    {
        Assert.Equal(expected, LockPolicy.Minutes(new System.Text.Json.Nodes.JsonObject { ["minutes"] = asked }));
    }

    [Fact]
    public async Task The_lock_ends_by_itself_when_the_time_is_up()
    {
        var expiry = new LockExpiry();
        var ended = new TaskCompletionSource();
        expiry.Start(TimeSpan.FromMilliseconds(50), () => { ended.SetResult(); return Task.CompletedTask; });
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_lock_that_was_released_does_not_end_again_and_a_new_lock_replaces_the_old_time()
    {
        var expiry = new LockExpiry();
        var count = 0;
        expiry.Start(TimeSpan.FromMilliseconds(80), () => { Interlocked.Increment(ref count); return Task.CompletedTask; });
        expiry.Cancel();
        await Task.Delay(300);
        Assert.Equal(0, count);

        expiry.Start(TimeSpan.FromMilliseconds(80), () => { Interlocked.Increment(ref count); return Task.CompletedTask; });
        expiry.Start(TimeSpan.FromMilliseconds(80), () => { Interlocked.Increment(ref count); return Task.CompletedTask; });
        await Task.Delay(400);
        Assert.Equal(1, count);   // the second Start replaced the first
    }
}
