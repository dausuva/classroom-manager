// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>The "Install Windows updates" schedule action: it belongs to the Maintenance module, needs --yes, and runs through the command line.</summary>
public sealed class UpdateInstallScheduleTests
{
    [Fact]
    public void The_action_comes_from_the_maintenance_module_and_says_it_never_restarts()
    {
        Assert.Equal(BuiltInModules.Maintenance, BuiltInModules.ActionOwner("updateinstall"));
        Assert.Contains(BuiltInModules.ModuleActions, action => action.Code == "updateinstall" && action.Label.Contains("never restarts"));
    }

    [Fact]
    public void A_schedule_with_it_catches_up_so_it_runs_at_all()
    {
        // Module actions run only through the catch-up path of the command line, so the action must always catch up.
        Assert.True(ScheduleRuns.CatchesUp(new ScheduledAction { Action = "updateinstall" }));
    }

    [Fact]
    public void The_command_line_needs_yes_to_install_and_a_schedule_with_it_needs_yes_too()
    {
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["updateinstall", "--computer", "ACER-WIN10"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["updateinstall"]));
        Assert.Equal("updateinstall", CliParser.Parse(["updateinstall", "--computer", "ACER-WIN10", "--yes"]).Command);

        var schedule = new ScheduledAction { Name = "Install updates", Action = "updateinstall" };
        Assert.Throws<CliUsageException>(() => CliSchedules.ToCommand(schedule, CliParser.Parse(["schedule", "run", "--name", "x"])));
        Assert.Equal("updateinstall", CliSchedules.ToCommand(schedule, CliParser.Parse(["schedule", "run", "--name", "x", "--yes"])).Command);
        Assert.Contains("updateinstall", CliParser.Usage);
    }
}
