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
    /// <summary>The window's own controls, from the saved settings: explanations, search, title, columns, Exam mode toggle, toolbar, host mode, log and operations views, the clock of the operations list, the network line and the list's source.</summary>
    void ConfigureWindow()
    {
        // Windows count their openings in these settings (saved with them), so an explanation folds after the first few times.
        UiPreferences.Instance.AlwaysShowExplanations = _settings.AlwaysShowExplanations;
        CommandSearchBox.Suggestions = CommandSearchResults;
        CommandSearchPopup.Closed += (_, _) => CommandSearchBox.UpdateSuggestionStatus("", false);
        Title = HostWindowTitle.Format(HostBuildInfo.Version);
        ApplyComputerColumnSettings();
        ExamModeToggle.IsChecked = _settings.ExamModeEnabled;
        UpdateExamModeVisualState();
        _lastFeedbackContext = FeedbackUi.Capture(this);
        BuildQuickActions();
        CaptureToolbar();
        ApplyToolbarLayout();
        ApplyHostMode();
        var copyLog = new ContextMenu();
        var copyLogItem = new MenuItem { Header = "Copy status" };
        copyLogItem.Click += (_, _) =>
        {
            if (LogList.SelectedItem is string message) Clipboard.SetText(message);
        };
        copyLog.Items.Add(copyLogItem);
        LogList.ContextMenu = copyLog;
        OperationsGrid.ItemsSource = _operations;
        // Ctrl+Tab changes the workspace view without a toggle click; the tables inside raise the same routed event.
        OperationsTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, OperationsTabs)) ShowWorkspaceView(OperationsTabs.SelectedIndex);
        };
        _recurringWork.Start("operations", TimeSpan.FromSeconds(1), () =>
        {
            _operationRuns.RefreshElapsed();
            UpdateOperationsViewLabel();
        });
        UpdateOperationsEmptyState();
        if (!HostSettings.SavesSuspended) _logFile.RemoveOld(DateTime.Now, _settings.ActivityLogKeepDays);
        RefreshNetworkScope();
        ClientGrid.ItemsSource = _clients;
        ClientGrid.LayoutUpdated += (_, _) => KeepColumnsReadable();
    }

    /// <summary>The computers remembered in the settings go into the list, in their rooms, and Exam mode's reach is completed for settings an older Host saved.</summary>
    void LoadSavedComputers()
    {
        foreach (var saved in _settings.Computers) _clients.Add(StartupRules.ClientFor(saved, _settings));
        RefreshRooms();
        UpdateCollectionState();
        StartupRules.CoverAssignedWhenUnrecorded(_settings, _clients);
        UpdateExamModeVisualState();
    }

    /// <summary>When the window has opened: the first steps for an empty Host, waiting messages, the daily look for a newer version, the schedules, the refresh timer and the first scan.</summary>
    async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        if (UiHooks.QuietStart) return;
        // A Host with nothing in it yet: lead through the first steps instead of opening on an empty list.
        if (FirstLaunchNotice.IsDue(_settings, _clients.Count)) ShowFirstLaunchNotice();
        switch (StartupRules.FirstStepFor(_settings.HasHostKeys, _clients.Count))
        {
            case StartupRules.FirstStep.SetupAssistant: await RunSetupAssistantAsync(firstStart: true); break;
            case StartupRules.FirstStep.SettingsDialog: Settings_Click(this, new RoutedEventArgs()); break;
        }
        AnnounceWaitingMessages();
        var updateCheck = _windowWork.RunAsync(LookForNewerVersionAsync);
        StartScheduler();
        StartAutoRefresh();
        await _windowWork.RunAsync(ScanAsync);
        await updateCheck;
    }
}
