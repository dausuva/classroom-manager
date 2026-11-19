// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>MW-H: what the open Host window does for a due schedule — which computers, which action, and the claim of the day before anything is sent.</summary>
[Collection(nameof(HostBackupTests))]
public sealed class GuiScheduleRunnerTests
{
    sealed class Recorder : IScheduledActions
    {
        public readonly List<string> Calls = [];
        public List<ClientInfo> LastTargets = [];

        Task Note(List<ClientInfo> targets, string call) { LastTargets = targets; Calls.Add(call); return Task.CompletedTask; }

        public Task LockAsync(List<ClientInfo> targets, string title, string text) => Note(targets, $"lock|{title}|{text}");
        public Task UnlockAsync(List<ClientInfo> targets) => Note(targets, "unlock");
        public Task MessageAsync(List<ClientInfo> targets, string title, string text, int seconds) => Note(targets, $"message|{title}|{text}|{seconds}");
        public Task WakeAsync(List<ClientInfo> targets) => Note(targets, "wake");
        public Task PowerAsync(List<ClientInfo> targets, string action, int delaySeconds, string message) => Note(targets, $"power|{action}|{delaySeconds}|{message}");
        public Task CommandAsync(List<ClientInfo> targets, string commandLine) => Note(targets, "command|" + commandLine);
        public Task TimeSyncAsync(List<ClientInfo> targets) => Note(targets, "timesync");
        public Task ApplyAppearancePresetAsync(List<ClientInfo> targets, string presetName) => Note(targets, "appearance|" + presetName);
    }

    static readonly ClientInfo[] Clients =
    [
        new() { Name = "A", Address = "10.0.0.1", Room = "410" },
        new() { Name = "B", Address = "10.0.0.2", Room = "410" },
        new() { Name = "C", Address = "10.0.0.3", Room = "411" },
    ];

    static (GuiScheduleRunner Runner, Recorder Actions, List<string> Log, HostSettings Settings) Make()
    {
        var settings = new HostSettings();
        var actions = new Recorder();
        var log = new List<string>();
        return (new GuiScheduleRunner(settings, actions, log.Add), actions, log, settings);
    }

    [Fact]
    public void A_schedule_covers_every_computer_a_room_or_the_saved_addresses()
    {
        Assert.Equal(["A", "B", "C"], GuiScheduleRunner.Targets(new ScheduledAction { AllComputers = true }, Clients).Select(c => c.Name));
        Assert.Equal(["A", "B"], GuiScheduleRunner.Targets(new ScheduledAction { Room = "410" }, Clients).Select(c => c.Name));
        Assert.Equal(["C"], GuiScheduleRunner.Targets(new ScheduledAction { Addresses = ["10.0.0.3"] }, Clients).Select(c => c.Name));
        Assert.Equal(["C"], GuiScheduleRunner.Targets(new ScheduledAction { Addresses = ["10.0.0.3", "10.0.0.99"] }, Clients).Select(c => c.Name));
        Assert.Empty(GuiScheduleRunner.Targets(new ScheduledAction(), Clients));
    }

    [Theory]
    [InlineData("lock", "lock|Screen locked|Eyes up")]
    [InlineData("unlock", "unlock")]
    [InlineData("message", "message||Eyes up|0")]
    [InlineData("wake", "wake")]
    [InlineData("shutdown", "power|shutdown|120|" + GuiScheduleRunner.ShutdownMessage)]
    [InlineData("restart", "power|restart|120|" + GuiScheduleRunner.RestartMessage)]
    [InlineData("sleep", "power|sleep|0|")]
    [InlineData("command", "command|Eyes up")]
    [InlineData("timesync", "timesync")]
    [InlineData("appearance", "appearance|Eyes up")]
    public async Task Each_action_is_the_one_the_window_has_with_the_same_arguments(string action, string expected)
    {
        var (runner, actions, _, _) = Make();

        await runner.PerformAsync(new ScheduledAction { Action = action, Text = " Eyes up ".Trim() }, [Clients[0]]);

        Assert.Equal([expected], actions.Calls);
    }

    [Fact]
    public async Task A_lock_uses_the_schedule_s_own_title_when_it_has_one()
    {
        var (runner, actions, _, _) = Make();

        await runner.PerformAsync(new ScheduledAction { Action = "lock", Title = "Break", Text = "Back at 10" }, [Clients[0]]);

        Assert.Equal(["lock|Break|Back at 10"], actions.Calls);
    }

    [Fact]
    public async Task An_action_the_window_does_not_run_itself_does_nothing()
    {
        var (runner, actions, _, _) = Make();

        await runner.PerformAsync(new ScheduledAction { Action = "backup" }, [Clients[0]]);
        await runner.PerformAsync(new ScheduledAction { Action = "something-new" }, [Clients[0]]);

        Assert.Empty(actions.Calls);
    }

    [Fact]
    public async Task A_due_schedule_claims_the_day_logs_what_it_will_do_and_does_it_on_its_computers()
    {
        var (runner, actions, log, settings) = Make();
        var schedule = new ScheduledAction { Name = "Morning", Action = "unlock", Room = "410" };

        await runner.RunAsync(schedule, new DateTime(2026, 11, 2, 8, 0, 0), Clients);

        Assert.Equal("2026-11-02", schedule.LastRunDate);
        Assert.Equal(["unlock"], actions.Calls);
        Assert.Equal(["A", "B"], actions.LastTargets.Select(c => c.Name));
        Assert.Single(log);
        Assert.StartsWith("Schedule \"Morning\": ", log[0]);
        Assert.EndsWith(" on 2 computer(s)", log[0]);
        Assert.NotNull(settings);
    }

    [Fact]
    public async Task A_schedule_with_no_computers_still_claims_the_day_and_sends_nothing()
    {
        var (runner, actions, log, _) = Make();
        var schedule = new ScheduledAction { Name = "Empty", Action = "wake", Room = "none" };

        await runner.RunAsync(schedule, new DateTime(2026, 11, 2, 8, 0, 0), Clients);

        Assert.Equal("2026-11-02", schedule.LastRunDate);
        Assert.Empty(actions.Calls);
        Assert.EndsWith(" on 0 computer(s)", log.Single());
    }
}
