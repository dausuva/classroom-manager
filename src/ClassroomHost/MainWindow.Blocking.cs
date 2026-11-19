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
    async void BlockApps_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;

        var dlg = new FormDialog("Block apps", this)
            .Target(targets)
            .LoadAsync($"Loading running apps and existing block lists from {targets.Count} computer(s)...", async form =>
            {
                var inventories = await Task.WhenAll(targets.Select(LoadAppBlockInventoryAsync));
                var summary = AppBlockListSummary.Create(
                    inventories.Where(item => item.BlockedNames is not null).Select(item => item.BlockedNames!), targets.Count);
                var currentApps = summary.Union;
                var runningApps = inventories.SelectMany(item => item.RunningApps)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var choices = _settings.BlockedAppCatalog.Concat(runningApps).Concat(currentApps)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
                var failures = inventories.Where(item => item.Errors.Count > 0).ToList();

                form.Note($"Read app blocks from {summary.AvailableCount} of {targets.Count} selected computer(s). " +
                          $"They currently use {summary.DistinctListCount} distinct list(s). " +
                          (summary.UnavailableCount == 0 ? "" : $"{summary.UnavailableCount} list(s) were unavailable. ") +
                          "Applying replaces the permanent app block on every selected computer, synchronizing them to the chosen names.");
                if (failures.Count > 0)
                    form.Note("Inventory warnings:\n" + string.Join("\n", failures.Select(item =>
                        $"• {item.Client.Name}: {string.Join("; ", item.Errors)}")));
                form.MultiSelect("selectedNames", "Host defaults, blocked names from any selected computer, and currently running apps", choices, currentApps, filter: true);
                form.Text("names", "Additional process names or full executable paths (one per line or comma-separated)", multiline: true)
                    .Check("applyDefaults", "Include every name from the Host default app block list")
                    .Check("remember", "Add the applied names to the Host default app block list", true)
                    .Check("replaceCatalog", "Replace the Host default app block list with the applied names")
                    .Check("clear", "Clear the block instead (stop ending these processes)")
                    .Note("The client checks every few seconds and ends any matching process it finds - including one " +
                          "already running when you apply this, and one the student tries to reopen. A process name blocks that name at any location; a full path blocks only that executable.");
            });
        if (!dlg.Ask("Apply")) return;

        var plan = AppBlockPlan.Build(dlg.B("clear"), dlg.S("selectedNames"), dlg.S("names"), dlg.B("applyDefaults"), _settings.BlockedAppCatalog, dlg.B("remember"), dlg.B("replaceCatalog"));
        if (plan.Problem is { } problem) { MessageBox.Show(this, problem); return; }

        var targetText = OperationTargetSummary.Format(targets);
        var appReview = new BulkOperationReview(
            "Review app restriction",
            targetText.Heading + "\n" + targetText.Detail,
            plan.ReviewItems,
            "Run progress and failed computers remain available in Operations. Safely repeatable failures can be retried independently.",
            plan.Warning,
            "Apply restriction");
        if (new BulkOperationReviewWindow(this, appReview).Ask() != BulkOperationReviewDecision.Run) return;

        if (plan.CatalogAfter(_settings.BlockedAppCatalog) is { } catalog)
        {
            _settings.BlockedAppCatalog = catalog;
            _settings.Save();
        }

        await RunOnAsync(targets, plan.Title, async (c, s) =>
        {
            if (plan.Clear) await s.CallAsync(Cmd.BlockAppsClear);
            else await s.CallAsync(Cmd.BlockAppsSet, new JsonObject { ["names"] = new JsonArray(plan.Names.Select(n => (JsonNode)n).ToArray()) });
        }, allowRetry: true, warnings: appReview.Warning);
    }

    async void ExamModeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_changingExamMode) return;
        var enable = ExamModeToggle.IsChecked == true;
        await ConfirmExamModeChangeAsync(enable);
    }

    async Task ConfirmExamModeChangeAsync(bool enable)
    {
        var deviceControls = ExamDeviceControls.Describe(
            _settings.ExamModeMuteAudio, _settings.ExamModeDisableCamera, _settings.ExamModeBlockUsbStorage);
        if (ExamModeChangePrompt.NothingToApply(enable, _settings.ExamModeDomains.Count, _settings.ExamModeApps.Count, deviceControls))
        {
            MessageBox.Show(this, ExamModeChangePrompt.NothingConfigured,
                "Exam mode", MessageBoxButton.OK, MessageBoxImage.Information);
            SetExamModeToggle(false);
            return;
        }
        // On: the assigned computers shown now. Off: the computers it was turned on for, whatever is shown now.
        var targets = enable ? ExamModeTargets() : ExamModeComputers();
        if (enable && targets.Count == 0)
        {
            MessageBox.Show(this, ExamModeChangePrompt.NoComputers, "Exam mode");
            SetExamModeToggle(!enable);
            return;
        }

        if (MessageBox.Show(this,
                ExamModeChangePrompt.Message(enable, targets.Count, _settings.ExamModeDomains.Count, _settings.ExamModeApps.Count, deviceControls),
                ExamModeChangePrompt.Caption(enable), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            SetExamModeToggle(!enable);
            return;
        }
        if (!await ApplyExamModeAsync(enable))
        {
            SetExamModeToggle(!enable);
        }
    }

    void SetExamModeToggle(bool enabled)
    {
        _changingExamMode = true;
        ExamModeToggle.IsChecked = enabled;
        _changingExamMode = false;
        UpdateExamModeVisualState();
    }

    void UpdateExamModeVisualState()
    {
        var enabled = ExamModeToggle.IsChecked == true;
        ComputerListBorder.BorderThickness = enabled ? new Thickness(3) : new Thickness(0);
        ComputerListBorder.Padding = enabled ? new Thickness(2) : new Thickness(0);
        if (enabled)
            ComputerListBorder.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        else
            ComputerListBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
        ComputerListBorder.ToolTip = enabled
            ? $"Exam mode is active for {ExamModeComputers().Count} computer(s), in whichever room they are."
            : null;
    }

    /// <summary>The assigned computers shown now: what turning Exam mode on applies to.</summary>
    List<ClientInfo> ExamModeTargets() =>
        _view.Cast<ClientInfo>().Where(client => client.Room.Length > 0).ToList();

    /// <summary>The computers Exam mode was turned on for, whichever room or network is shown now.</summary>
    List<ClientInfo> ExamModeComputers() => _exam.Computers(_clients);

    /// <param name="reapply">Exam mode is already on and only its settings changed: keep the same computers.</param>
    async Task<bool> ApplyExamModeAsync(bool enable, bool reapply = false)
    {
        var members = enable && !reapply ? ExamModeTargets() : ExamModeComputers();
        if (enable && members.Count == 0)
        {
            MessageBox.Show(this, ExamModeChangePrompt.NoComputers, "Exam mode");
            return false;
        }
        // Computers that have not answered yet cannot be sent anything now. They stay on the list:
        // the status refresh turns Exam mode on, or off again, as soon as each one answers.
        var targets = members.Where(client => client.Version.Length > 0).ToList();
        var command = enable ? Cmd.ExamModeSet : Cmd.ExamModeClear;
        if (targets.Count > 0 && !EnsureFeatureSupported(targets, command, "Exam mode")) return false;
        _exam.Begin(enable, reapply, members);
        var overlay = _exam.OverlayArguments();
        if (targets.Count > 0)
            await RunOnAsync(targets, enable ? "Enable Exam mode" : "Disable Exam mode",
                (client, session) => _exam.ApplyAsync(client, new ExamPeer(session), enable, overlay,
                    (computer, result) => UpdateBlockedWebsiteCount(computer, result, "effectiveWebsiteCount")),
                allowRetry: true);
        var waiting = _exam.Waiting(enable, members);
        if (waiting > 0)
            Log(enable
                ? $"Exam mode: {waiting} computer(s) did not get it yet; it is applied when they answer a status refresh."
                : $"Exam mode: {waiting} computer(s) could not be reached; it is turned off there when they answer a status refresh.");
        SetExamModeToggle(enable);
        return true;
    }

    async Task<AppBlockInventory> LoadAppBlockInventoryAsync(ClientInfo client)
    {
        var errors = new List<string>();
        var runningApps = new List<string>();
        IReadOnlyList<string>? blockedNames = null;
        try
        {
            using var session = await ClientSession.OpenAsync(client, _settings);
            try
            {
                var data = await session.CallAsync(Cmd.ProcList);
                runningApps = data["processes"]!.AsArray()
                    .Select(item => item?["path"]?.GetValue<string>() is { Length: > 0 } path
                        ? path
                        : (item?["name"]?.GetValue<string>() ?? "") + ".exe")
                    .Where(name => name.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                errors.Add("running apps unavailable: " + ex.Message);
                Log($"Could not load process names for Block apps from {client.Name}: {ex.Message}");
            }

            if (!client.SupportsCommand(Cmd.BlockAppsList))
            {
                errors.Add($"app block list requires client protocol {Protocol.RequiredVersion(Cmd.BlockAppsList)}");
            }
            else
            {
                try
                {
                    var data = await session.CallAsync(Cmd.BlockAppsList);
                    blockedNames = data["names"]?.AsArray().Select(node => node?.GetValue<string>() ?? "")
                        .Where(value => value.Length > 0).ToList() ?? [];
                }
                catch (Exception ex)
                {
                    errors.Add("app block list unavailable: " + ex.Message);
                    Log($"Could not load the existing app block from {client.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add("computer unavailable: " + ex.Message);
            Log($"Could not load Block apps inventory from {client.Name}: {ex.Message}");
        }
        return new AppBlockInventory(client, runningApps, blockedNames, errors);
    }

    sealed record AppBlockInventory(ClientInfo Client, IReadOnlyList<string> RunningApps,
        IReadOnlyList<string>? BlockedNames, IReadOnlyList<string> Errors);

    async void Inventory_Click(object sender, RoutedEventArgs e)
    {
#if REPORTING_MODULE
        if (!BuiltInModules.Enabled(_settings, BuiltInModules.Reporting)) { MessageBox.Show(this, BuiltInModules.Disabled); return; }
        var targets = Selected();
        if (targets.Count == 0) return;

        var dlg = ReportingPresentation.Form(this, targets);
        if (!dlg.Ask("Export")) return;
        var folder = dlg.S("folder");
        if (folder.Length == 0 || !System.IO.Directory.Exists(folder)) { MessageBox.Show(this, ReportingPresentation.ChooseFolder); return; }

        var reportTargets = targets.ToDictionary(client => client,
            client => new InventoryReportTarget(Guid.NewGuid().ToString("N"), client.Name));
        var report = InventoryReportingModule.CreateReport(reportTargets.Values);
        var export = new InventoryReportExport(report, folder, DateTime.Now);

        await RunOnAsync(targets, ReportingPresentation.Collect, async (c, s) =>
        {
            var (reporting, machine) = ModuleRuns.OpenReporting(s, c);
            var data = JsonNode.Parse(await reporting.InventoryAsync(machine, CancellationToken.None))!.AsObject();
            export.Include(reportTargets[c].Id, InventoryReportData.Capture(data));
        }, allowRetry: true, englishTitle: ReportingPresentation.CollectEnglish);

        try
        {
            var csv = export.Write();
            Log(ReportingPresentation.Written(csv.Computers, export.HardwarePath, csv.Programs, export.SoftwarePath, english: true),
                ReportingPresentation.Written(csv.Computers, export.HardwarePath, csv.Programs, export.SoftwarePath));
        }
        catch (Exception ex) { MessageBox.Show(this, ReportingPresentation.SaveFailed(ex.Message)); }
#else
        await Task.CompletedTask;
#endif
    }

    // ---- remote --------------------------------------------------------------------------
}
