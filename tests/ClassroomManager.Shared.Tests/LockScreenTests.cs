// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class LockScreenTests
{
    [Fact]
    public void The_user_lock_screen_reset_runs_as_the_user_needs_an_admin_key_and_is_new_in_1_12()
    {
        Assert.True(Cmd.IsUserOnly(Cmd.LockScreenReset));
        Assert.Equal("1.12", Protocol.RequiredVersion(Cmd.LockScreenReset));
        Assert.Contains(Cmd.LockScreenReset, Protocol.Capabilities);
        // Like signin.image.default, which the Host sends first.
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(Cmd.LockScreenReset));
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(Cmd.SignInImageDefault));
    }

    [Fact]
    public void Setting_the_user_lock_screen_runs_as_the_user_needs_an_admin_key_and_is_new_in_1_13()
    {
        Assert.True(Cmd.IsUserOnly(Cmd.LockScreenSet));
        Assert.Equal("1.13", Protocol.RequiredVersion(Cmd.LockScreenSet));
        Assert.Contains(Cmd.LockScreenSet, Protocol.Capabilities);
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(Cmd.LockScreenSet));
        // A 1.12 client does not advertise it, so the Host does not ask it for per-user browser policies either.
        Assert.False(Protocol.SupportsCommand(Cmd.LockScreenSet, "1.12", [Cmd.LockScreenReset], capabilitiesAdvertised: true));
    }
}
