// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;

namespace ClassroomManager.Host;

/// <summary>What the Host window does when a schedule is due; <c>MainWindow</c> implements it with the same <c>Do...Async</c> actions its buttons use.</summary>
public interface IScheduledActions
{
    Task LockAsync(List<ClientInfo> targets, string title, string text);
    Task UnlockAsync(List<ClientInfo> targets);
    Task MessageAsync(List<ClientInfo> targets, string title, string text, int seconds);
    Task WakeAsync(List<ClientInfo> targets);
    Task PowerAsync(List<ClientInfo> targets, string action, int delaySeconds, string message);
    Task CommandAsync(List<ClientInfo> targets, string commandLine);
    Task TimeSyncAsync(List<ClientInfo> targets);
    Task ApplyAppearancePresetAsync(List<ClientInfo> targets, string presetName);
}

/// <summary>
/// The scheduling adapter of the Host window (MW-H): the computers a due schedule covers and the action it performs on them, while the window is open. The
/// timing, the once-a-day claim and the catch-up are <c>ScheduleWorkflow</c> and <c>ScheduleRuns</c>; the same schedules run from <c>ClassroomCli</c> when the
/// Host is closed (<see cref="CliSchedules"/>). The messages of the power actions are the ones the command line uses.
/// </summary>
public sealed class GuiScheduleRunner(HostSettings settings, IScheduledActions actions, Action<string> log)
{
    public const string ShutdownMessage = "This computer will shut down in 2 minutes. Please save your work.";
    public const string RestartMessage = "This computer will restart in 2 minutes. Please save your work.";

    /// <summary>Every computer, a room's computers, or the computers with the saved addresses, from the ones the window lists.</summary>
    public static List<ClientInfo> Targets(ScheduledAction schedule, IEnumerable<ClientInfo> clients) =>
        schedule.AllComputers ? clients.ToList()
        : schedule.Room.Length > 0 ? clients.Where(client => client.Room == schedule.Room).ToList()
        : clients.Where(client => schedule.Addresses.Contains(client.Address, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Claims the day for the schedule (saved before anything is sent), logs it, and performs its action on the computers it covers.</summary>
    public async Task RunAsync(ScheduledAction schedule, DateTime now, IEnumerable<ClientInfo> clients)
    {
        schedule.LastRunDate = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        settings.Save();

        var targets = Targets(schedule, clients);
        log($"Schedule \"{schedule.Name}\": {SchedulesWindow.ActionLabel(schedule.Action)} on {targets.Count} computer(s)");
        if (targets.Count == 0) return;
        await PerformAsync(schedule, targets);
    }

    /// <summary>The action of the schedule. An action this window does not run itself (module actions that catch up through the command line) does nothing here.</summary>
    public Task PerformAsync(ScheduledAction schedule, List<ClientInfo> targets) => schedule.Action switch
    {
        "lock" => actions.LockAsync(targets, schedule.Title.Length > 0 ? schedule.Title : "Screen locked", schedule.Text),
        "unlock" => actions.UnlockAsync(targets),
        "message" => actions.MessageAsync(targets, schedule.Title, schedule.Text, 0),
        "wake" => actions.WakeAsync(targets),
        "shutdown" => actions.PowerAsync(targets, "shutdown", 120, ShutdownMessage),
        "restart" => actions.PowerAsync(targets, "restart", 120, RestartMessage),
        "sleep" => actions.PowerAsync(targets, "sleep", 0, ""),
        "command" => actions.CommandAsync(targets, schedule.Text),
        "timesync" => actions.TimeSyncAsync(targets),
        "appearance" => actions.ApplyAppearancePresetAsync(targets, schedule.Text.Trim()),
        _ => Task.CompletedTask,
    };
}
