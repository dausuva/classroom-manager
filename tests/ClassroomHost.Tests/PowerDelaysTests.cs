// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class PowerDelaysTests
{
    [Fact]
    public void A_restart_is_suggested_in_five_seconds_and_a_shutdown_in_a_minute()
    {
        Assert.Equal("5", PowerDelays.Default("Restart"));
        Assert.Equal("60", PowerDelays.Default("Shut down"));
        Assert.Equal("60", PowerDelays.Default("Sleep"));
    }

    [Fact]
    public void A_five_second_restart_is_too_short_to_be_undone()
    {
        Assert.True(int.Parse(PowerDelays.Default("Restart")) < PowerDelays.UndoMinimum);
        Assert.True(int.Parse(PowerDelays.Default("Shut down")) >= PowerDelays.UndoMinimum);
    }
}
