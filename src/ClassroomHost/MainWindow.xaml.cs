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

public partial class MainWindow : Window, IManualDiscoveryView, IFeedbackSecretSource
{
    IEnumerable<string> IFeedbackSecretSource.FeedbackSecrets =>
        _settings.CurrentHostKeys().Select(key => key.Psk).Concat(_settings.PreviousPsks);
    readonly HostSettings _settings;
    readonly ObservableCollection<ClientInfo> _clients = new();
    readonly OperationsController _operationRuns;
    readonly ExamModeWorkflow _exam;
    readonly KeyRotationWorkflow _keys;
    readonly GuiScheduleRunner _schedules;
    ObservableCollection<OperationActivity> _operations => _operationRuns.Operations;
    readonly ICollectionView _view;
    readonly RecurringWorkLifetime _recurringWork;
    readonly HostWindowCallbacks _windowCallbacks;
    readonly WindowWorkLifetime _windowWork = new();
    readonly StatusRefreshWorkflow _statusRefresh = new();
    readonly StatusProbe _statusProbe = new();
    readonly DiscoveryWorkCoordinator _discoveryWork = new(AddressFollower.Interval);
    readonly HostRosterPersistence _rosterPersistence;
    readonly HostRoster _roster;
    readonly ManualDiscoveryController _manualDiscovery;
    readonly AppearanceFeatureController _appearance;
    readonly SoftwareDeploymentController _softwareDeployment;
    string _room = "";
    bool _changingExamMode;
    FeedbackContext? _lastFeedbackContext;
    Discovery.NetworkSubnet? _networkScope;
    /// <summary>This host's own upload folder on the student PCs.</summary>
    string MyStaging => StagingNamespace.Root(_settings);

    public MainWindow() : this(HostSettings.Load()) { }

    internal MainWindow(HostSettings settings)
    {
        _settings = settings;
        _logFile = new(knownSecrets: () => ((IFeedbackSecretSource)this).FeedbackSecrets);
        _operationRuns = new OperationsController(settings, Log);
        _exam = new ExamModeWorkflow(settings);
        _keys = new KeyRotationWorkflow(settings);
        _schedules = new GuiScheduleRunner(settings, new ScheduledActionsAdapter(this), message => Log(message));
        _operationRuns.Added += operation =>
        {
            OperationsGrid.SelectedItem = operation;
            UpdateOperationsEmptyState();
        };
        InitializeComponent();
        if (HostSelfInstall.DetectHere() == SelfInstallState.Offered) InstallHereButton.Visibility = Visibility.Visible;
        BuiltInModules.LogSink = line => Dispatcher.BeginInvoke(() => Log(line));
#if !REPORTING_MODULE
        ExportInventoryButton.Visibility = Visibility.Collapsed;
        ExportInventoryButton.IsEnabled = false;
        if (ExportInventoryButton.Parent is Panel inventoryPanel) inventoryPanel.Children.Remove(ExportInventoryButton);
#endif
        _rosterPersistence = new HostRosterPersistence(_settings);
        _roster = new HostRoster(_clients);
        _appearance = new AppearanceFeatureController(this, _settings, Selected, EnsureFeatureSupported,
            (targets, title, action, retry, warnings, undo) => RunOnAsync(targets, title, action,
                allowRetry: retry, warnings: warnings, undo: undo));
        var dispatcher = Dispatcher;
        _recurringWork = new RecurringWorkLifetime((interval, tick) => new DispatcherRecurringTimer(dispatcher, interval, tick));
        Closed += (_, _) => _recurringWork.Dispose();
        _windowCallbacks = new HostWindowCallbacks(AskStepUpAsync, () => _clients.ToList(), window =>
        {
            var before = _settings.WindowOpenings.TryGetValue(window, out var count) ? count : 0;
            _settings.WindowOpenings[window] = Math.Min(before + 1, 1000);
            return before;
        });
        Closed += (_, _) => _windowCallbacks.Dispose();
        Closed += (_, _) => _windowWork.Dispose();
        Closed += (_, _) => _discoveryWork.Dispose();
        ConfigureWindow();
        _view = CollectionViewSource.GetDefaultView(_clients);
        _view.Filter = o => ClientMatchesFilters((ClientInfo)o);
        _manualDiscovery = new ManualDiscoveryController(this, _settings, _roster, _discoveryWork, SaveComputers);
        _softwareDeployment = new SoftwareDeploymentController(this, _settings, Selected,
            () => _view.Cast<ClientInfo>().ToList(), () => _room, EnsureFeatureSupported,
            (targets, title, action, batch, warnings) => RunOnAsync(targets, title, action, maxParallel: batch, warnings: warnings), Log);
        _clients.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateCollectionState);

        LoadSavedComputers();

        Loaded += OnWindowLoaded;
    }

    // ---- computers, rooms ------------------------------------------------------------

    List<ClientInfo> Selected()
    {
        var list = SelectedRaw();
        if (list.Count == 0) MessageBox.Show(this, "Select at least one computer first.");
        else if (list.Any(client => client.ExclusiveOperation.Length > 0))
        {
            MessageBox.Show(this,
                "A Classroom Client update is in progress on one or more selected computers. Other actions are paused until the update attempts finish.",
                "Action paused", MessageBoxButton.OK, MessageBoxImage.Information);
            return new List<ClientInfo>();
        }
        return list.Count == 0 ? list : LimitToLicence(list);
    }

    List<ClientInfo> SelectedRaw() => HostSelectionSnapshot.SelectTargets(_view.Cast<ClientInfo>(), _onlyFor);

    bool EnsureFeatureSupported(List<ClientInfo> targets, string command, string featureName)
    {
        var uncheckedTargets = targets.Where(c => c.Version.Length == 0).ToList();
        if (uncheckedTargets.Count > 0)
        {
            MessageBox.Show(this,
                $"Refresh status before using {featureName}. Compatibility has not been checked for:\n\n" +
                string.Join("\n", uncheckedTargets.Take(10).Select(c => c.Name)) +
                (uncheckedTargets.Count > 10 ? "\n..." : ""),
                "Client compatibility not checked", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var unsupported = targets.Where(c => !c.SupportsCommand(command)).ToList();
        if (unsupported.Count == 0) return true;

        var answer = MessageBox.Show(this,
            $"{featureName} is not supported by {unsupported.Count} selected computer(s):\n\n" +
            string.Join("\n", unsupported.Take(10).Select(c => $"{c.Name} — protocol {c.Version}")) +
            (unsupported.Count > 10 ? "\n..." : "") +
            $"\n\nThis feature requires Client protocol {Protocol.RequiredVersion(command)} or later. " +
            "Update the Client before trying again.\n\nOpen Update client now?",
            "Feature not supported", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes) UpdateClient_Click(this, new RoutedEventArgs());
        return false;
    }

    /// <summary>Runs an action against several clients in parallel and logs each result.</summary>
    Task RunOnAsync(List<ClientInfo> targets, string title, Func<ClientInfo, ClientSession, Task> action,
        bool quiet = false, int maxParallel = -1, bool allowExclusiveOperation = false, bool allowRetry = false,
        string warnings = "", Action<ClientInfo, string>? targetFinished = null,
        (string Label, Func<ClientInfo, ClientSession, Task> Action)? undo = null, string? englishTitle = null) =>
        _operationRuns.RunAsync(targets, title, action, quiet, maxParallel, allowExclusiveOperation, allowRetry, warnings, targetFinished, undo, englishTitle);

    void Log(string message) => Log(message, null);

    void Log(string message, string? shown)
    {
        var secrets = ((IFeedbackSecretSource)this).FeedbackSecrets.ToArray();
        message = DiagnosticText.OmitSecrets(message, secrets);
        if (shown is not null) shown = DiagnosticText.OmitSecrets(shown, secrets);
        var now = DateTime.Now;
        // Kept on disk as well (one file per day), because this view only holds the latest lines of this run.
        if (_settings.ActivityLogEnabled) _logFile.Write(now, message);
        // The file stays English, so a log can be read by whoever supports the lab; the view is in the chosen language.
        LogList.Items.Insert(0, $"{now:HH:mm:ss}  {shown ?? Loc.T(message)}");
        while (LogList.Items.Count > UiBufferLimits.ActivityEntries) LogList.Items.RemoveAt(LogList.Items.Count - 1);
        LogList.ScrollIntoView(LogList.Items[0]);
    }
}
