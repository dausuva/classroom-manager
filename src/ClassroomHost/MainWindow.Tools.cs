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
    void Programs_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        if (targets.Count == 1) new ProgramsWindow(targets[0], _settings, this, actions: ProgramActions()).Show();
        else ShowProgramsComparison(targets);
    }

    void Processes_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        if (targets.Count == 1) new ProcessesWindow(targets[0], _settings, this).Show();
        else new BulkProcessesWindow(targets, _settings, this, name => ShowProgramsComparison(targets, name)).Show();
    }

    ProgramWindowActions ProgramActions() => new(OpenProgramOnAsync, async computers => await _softwareDeployment.InstallAsync(computers.ToList()));

    void ShowProgramsComparison(IReadOnlyList<ClientInfo> targets, string filter = "")
    {
        if (!BuiltInModules.Enabled(_settings, BuiltInModules.Software)) return;
        new BulkProgramsWindow(targets, _settings, this,
            (operationTargets, title, action, maxParallel, warnings, targetFinished) =>
                RunOnAsync(operationTargets, title, action, maxParallel: maxParallel, warnings: warnings,
                    targetFinished: targetFinished), ProgramActions(), filter).Show();
    }

    void WindowsUpdate_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        if (!EnsureFeatureSupported(targets, Cmd.WindowsUpdateScan, "Windows Update management")) return;
        BulkOperationRunner run = (operationTargets, title, action, maxParallel, warnings, targetFinished) =>
            RunOnAsync(operationTargets, title, action, maxParallel: maxParallel, warnings: warnings, targetFinished: targetFinished);
        if (targets.Count == 1) new WindowsUpdateWindow(targets[0], _settings, this, run).ShowDialog();
        else new BulkWindowsUpdateWindow(targets, _settings, this, run).ShowDialog();
    }

    void Registry_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count != 1)
        {
            if (targets.Count > 1) MessageBox.Show(this, "Select exactly one computer to browse its registry.");
            return;
        }
        new RegistryWindow(targets[0], _settings, this).Show();
    }

    void Tasks_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        if (targets.Count == 1) new TasksWindow(targets[0], _settings, this).Show();
        else new BulkTasksWindow(targets, _settings, this).Show();
    }

    void Autoruns_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        if (targets.Count == 1) new AutorunsWindow(targets[0], _settings, this).Show();
        else new BulkAutorunsWindow(targets, _settings, this).Show();
    }

    void Accounts_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        if (targets.Count == 1) new AccountsWindow(targets[0], _settings, this).Show();
        else
        {
            var targetText = OperationTargetSummary.Format(targets);
            var review = new BulkOperationReview(
                "Review local accounts inventory",
                targetText.Heading + "\n" + targetText.Detail,
                [new("Action", "Read and compare local accounts"), new("Changes", "None — inventory is read-only")],
                "The results window keeps successful computers visible and identifies computers that could not be checked.",
                RunLabel: "Check accounts");
            if (new BulkOperationReviewWindow(this, review).Ask() == BulkOperationReviewDecision.Run)
                new BulkAccountsWindow(targets, _settings, this).Show();
        }
    }

    void SignedInAccounts_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        if (targets.Count == 1)
        {
            if (!EnsureFeatureSupported(targets, Cmd.AccountIdentityList, "Signed-in accounts")) return;
            new AccountIdentityWindow(targets[0], _settings, this).Show();
            return;
        }

        var targetText = OperationTargetSummary.Format(targets);
        var review = new BulkOperationReview(
            "Review signed-in account inventory",
            targetText.Heading + "\n" + targetText.Detail,
            [
                new("Action", "Read active Windows identities and Credential Manager target names"),
                new("Changes", "None — inventory is read-only"),
            ],
            "Successful computers remain visible when another computer is unavailable or has no signed-in user.",
            "Credential Manager target names are not passwords and are not a complete inventory of accounts inside every application.",
            "Check accounts");
        if (new BulkOperationReviewWindow(this, review).Ask() == BulkOperationReviewDecision.Run)
            new BulkAccountIdentityWindow(targets, _settings, this).Show();
    }

    void Terminal_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count != 1)
        {
            if (targets.Count > 1) MessageBox.Show(this, "Select exactly one computer to open a terminal.");
            return;
        }
        new TerminalWindow(targets[0], _settings, this).Show();
    }

    void KeyboardLayouts_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0 || !EnsureFeatureSupported(targets, Cmd.KeyboardList, "Keyboard layouts")) return;
        new KeyboardLayoutsWindow(targets, _settings, this,
            (operationTargets, title, action, maxParallel, warnings, targetFinished) =>
                RunOnAsync(operationTargets, title, action, maxParallel: maxParallel, warnings: warnings,
                    targetFinished: targetFinished)).Show();
    }
}
