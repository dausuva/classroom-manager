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
    void ClearCompletedOperations_Click(object sender, RoutedEventArgs e)
    {
        _operationRuns.ClearFinished();
        UpdateOperationsEmptyState();
    }

    void UpdateOperationsEmptyState() =>
        OperationsEmptyState.Visibility = _operations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void CopyOperationDetails_Click(object sender, RoutedEventArgs e)
    {
        if (OperationsGrid.SelectedItem is not OperationActivity operation)
        {
            MessageBox.Show(this, "Select an operation to copy its details.", "Copy operation details",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Clipboard.SetText(operation.FormatDetails());
    }

    void OpenOperationResults_Click(object sender, RoutedEventArgs e) => ShowOperationResults(true);

    void CloseOperationResults_Click(object sender, RoutedEventArgs e) => ShowOperationResults(false);

    void OperationsGrid_DoubleClick(object sender, MouseButtonEventArgs e) =>
        ShowOperationResults(OperationResultsPanel.Visibility != Visibility.Visible);

    /// <summary>
    /// The per-computer results of the selected operation take most of the view, so they are folded
    /// away until asked for and can be folded away again; the list of operations stays readable.
    /// </summary>
    void ShowOperationResults(bool open)
    {
        if (open && OperationsGrid.SelectedItem is not OperationActivity)
        {
            MessageBox.Show(this, "Select an operation to see its results.", "Open results", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // A panel beside the list, not row details inside it: the computers get their own table and scroll bar.
        OperationResultsPanel.Visibility = OperationResultsSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        OperationResultsSplitterColumn.Width = open ? GridLength.Auto : new GridLength(0);
        OperationResultsColumn.Width = open ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        // Results follow the selection once open, so there is nothing more to open until they are closed.
        OpenResultsButton.IsEnabled = !open;
        CloseResultsButton.IsEnabled = open;
        if (!open) return;
        ShowWorkspaceView(OperationsViewIndex);
        OperationsGrid.ScrollIntoView(OperationsGrid.SelectedItem);
        OperationsGrid.Focus();
    }

    // ---- log view ---------------------------------------------------------------------------------

    readonly ActivityLogFile _logFile;

    string LogText() => string.Join(Environment.NewLine, LogList.Items.Cast<object>().Select(item => item.ToString()));

    /// <summary>The activity log part of Host settings, on its own, where the log is read.</summary>
    void LoggingOptions_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new FormDialog("Logging options", this)
            .Check("activityLog", "Keep an activity log on this computer (every line of the Log view, one file per day)", _settings.ActivityLogEnabled)
            .Combo("activityLogDays", "Keep activity log files for", ActivityLogFile.RetentionChoices.Select(ActivityLogFile.RetentionLabel).ToArray(),
                Math.Max(0, Array.IndexOf(ActivityLogFile.RetentionChoices, _settings.ActivityLogKeepDays)));
        if (!dlg.Ask("Save")) return;
        _settings.ActivityLogEnabled = dlg.B("activityLog");
        _settings.ActivityLogKeepDays = ActivityLogFile.ParseRetention(dlg.S("activityLogDays"));
        // As in Host settings: a shorter period takes effect now; switching the log off keeps the files until they reach that age.
        _logFile.RemoveOld(DateTime.Now, _settings.ActivityLogKeepDays);
        _settings.Save();
        Log(_settings.ActivityLogEnabled
            ? $"Activity log on; files are kept for {ActivityLogFile.RetentionLabel(_settings.ActivityLogKeepDays)}."
            : "Activity log off; nothing new is written to the log files.");
    }

    void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"classroom-host-log-{DateTime.Now:yyyy-MM-dd-HHmm}.txt", Filter = "Text file (*.txt)|*.txt",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            // The view shows times only; the file says which day they belong to.
            System.IO.File.WriteAllText(dlg.FileName,
                $"Classroom Host log, exported {DateTime.Now:yyyy-MM-dd HH:mm}{Environment.NewLine}{LogText()}{Environment.NewLine}");
            Log($"Log exported to {dlg.FileName}");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Export log", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_logFile.Directory);
            Process.Start(new ProcessStartInfo(_logFile.Directory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "Open log folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.Items.Count > 0) Clipboard.SetText(LogText());
    }

    const int ComputersViewIndex = 0;
    const int OperationsViewIndex = 1;
    const int LogViewIndex = 2;

    void ComputersView_Click(object sender, RoutedEventArgs e) => ShowWorkspaceView(ComputersViewIndex);

    void OperationsView_Click(object sender, RoutedEventArgs e) => ShowWorkspaceView(OperationsViewIndex);

    void LogView_Click(object sender, RoutedEventArgs e) => ShowWorkspaceView(LogViewIndex);

    /// <summary>Switches the workspace below the quick actions between its three full-height views.</summary>
    void ShowWorkspaceView(int index)
    {
        OperationsTabs.SelectedIndex = index;
        ComputersViewButton.IsChecked = index == ComputersViewIndex;
        OperationsViewButton.IsChecked = index == OperationsViewIndex;
        LogViewButton.IsChecked = index == LogViewIndex;
        // Each view shows only the actions that apply to it.
        ComputerListActionsPanel.Visibility = index == ComputersViewIndex ? Visibility.Visible : Visibility.Collapsed;
        OperationActionsPanel.Visibility = index == OperationsViewIndex ? Visibility.Visible : Visibility.Collapsed;
        LogActionsPanel.Visibility = index == LogViewIndex ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Only the badge changes: the caption is set once, so it is not re-translated (and does not flicker) every second.</summary>
    void UpdateOperationsViewLabel()
    {
        var running = _operationRuns.Running;
        var badge = OperationViewLabel.Badge(running);
        if (OperationsBadgeText.Text == badge) return;
        OperationsBadgeText.Text = badge;
        OperationsBadge.Visibility = badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(OperationsViewButton, OperationViewLabel.Status(running));
    }

    void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        if (OperationsGrid.SelectedItem is OperationActivity operation) operation.Cancel();
    }

    async void RetryFailedOperation_Click(object sender, RoutedEventArgs e)
    {
        if (OperationsGrid.SelectedItem is OperationActivity operation && operation.CanRetry)
            await operation.RetryFailedAsync();
    }

    async void UndoOperation_Click(object sender, RoutedEventArgs e)
    {
        if (OperationsGrid.SelectedItem is not OperationActivity operation || !operation.CanUndo) return;
        if (MessageBox.Show(this, $"Take \"{operation.Title}\" back on the {operation.Succeeded} computer(s) it succeeded on?\n\nThis runs: {operation.UndoLabel}.",
                "Undo", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await operation.UndoAsync();
    }

    void ReviewOperationWarnings_Click(object sender, RoutedEventArgs e)
    {
        if (OperationsGrid.SelectedItem is not OperationActivity { HasWarnings: true } operation) return;
        MessageBox.Show(this, operation.Warnings, operation.Title + " — warnings",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
