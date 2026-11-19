// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ClassroomManager.Shared;
#if BROWSER_INVENTORY_MODULE
using ClassroomManager.BrowserInventory;
#endif
#if REPORTING_MODULE
using ClassroomManager.Reporting;
#endif

namespace ClassroomManager.Host;

public partial class MainWindow
{
    void Schedules_Click(object sender, RoutedEventArgs e)
    {
        if (!BuiltInModules.Enabled(_settings, BuiltInModules.Automation)) return;
        new SchedulesWindow(_settings, this,
            () => ClientGrid.SelectedItems.Cast<ClientInfo>().Select(c => c.Address).ToList(),
            Rooms).Show();
    }

    void StartScheduler()
    {
        if (!_recurringWork.Start("schedules", TimeSpan.FromSeconds(20), async () => await RunDueSchedulesAsync())) return;
        // The task names this program; after an update or a move it is pointed at the program as it is now.
        if (_settings.RunSchedulesWhenClosed && _settings.RunSchedules && Environment.ProcessPath is { } program)
            _ = Task.Run(() => BackgroundSchedules.Apply(true, Environment.UserName, program)).ContinueWith(done =>
            {
                if (done.Result is { } problem) Dispatcher.Invoke(() => Log("Schedules when the Host is closed: " + problem));
            }, TaskScheduler.Default);
        _ = RunDueSchedulesAsync();
    }

    bool _schedulerBusy;

    async Task RunDueSchedulesAsync()
    {
        if (_schedulerBusy || !_settings.RunSchedules) return;
        _schedulerBusy = true;
        try
        {
            var now = DateTime.Now;
            var workflow = new ScheduleWorkflow(ScheduleRuns.FilePath, ScheduleRuns.ComputersFilePath);
            await workflow.RunTimedAsync(BuiltInModules.AdmittedSchedules(_settings), now,
                (sch, _) => _schedules.RunAsync(sch, now, _clients), (_, ex) => Log("Scheduler error: " + ex.Message));

            // Catch-up schedules: the computers that missed the latest run get it now if they answer, also right after the Host starts.
            if (_settings.Schedules.Any(ScheduleRuns.CatchesUp))
                await HostCli.RunCatchUpAsync(_settings, CliParser.Parse(["schedule", "run-due", "--yes"]), now, Log);
        }
        catch (Exception ex) { Log("Scheduler error: " + ex.Message); }
        finally { _schedulerBusy = false; }
    }

    /// <summary>The window's own actions, as the scheduling adapter calls them.</summary>
    sealed class ScheduledActionsAdapter(MainWindow owner) : IScheduledActions
    {
        public Task LockAsync(List<ClientInfo> targets, string title, string text) => owner.DoLockAsync(targets, title, text);
        public Task UnlockAsync(List<ClientInfo> targets) => owner.DoUnlockAsync(targets);
        public Task MessageAsync(List<ClientInfo> targets, string title, string text, int seconds) => owner.DoMessageAsync(targets, title, text, seconds);
        public Task WakeAsync(List<ClientInfo> targets) => owner.DoWakeAsync(targets);
        public Task PowerAsync(List<ClientInfo> targets, string action, int delaySeconds, string message) => owner.DoPowerAsync(targets, action, delaySeconds, message);
        public Task CommandAsync(List<ClientInfo> targets, string commandLine) => owner.DoCommandAsync(targets, commandLine);
        public Task TimeSyncAsync(List<ClientInfo> targets) => owner.DoTimeSyncAsync(targets, true, false);
        public Task ApplyAppearancePresetAsync(List<ClientInfo> targets, string presetName) => owner.DoAppearancePresetAsync(targets, presetName);
    }


    // ---- live status -----------------------------------------------------------------------------------
}
