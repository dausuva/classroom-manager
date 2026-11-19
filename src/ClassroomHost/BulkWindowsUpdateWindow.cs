// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

sealed class BulkUpdateItem
{
    public string Title { get; init; } = "";
    public string Details { get; init; } = "";
    public string? StoredDetails { get; init; }
    public string Display => Details.Length == 0 ? Title : $"{Title} — {Details}";
}

sealed class BulkUpdateResult : INotifyPropertyChanged
{
    string _status = "Waiting";
    string _pauseStatus = Loc.T("Pause status not checked");
    public string Computer { get; init; } = "";
    public string Address { get; init; } = "";
    public string CheckedUtc { get; set; } = "";
    public ObservableCollection<BulkUpdateItem> Items { get; } = new();
    public string Status { get => _status; set { if (_status == value) return; _status = value; Changed(); Changed(nameof(Header)); } }
    public string PauseStatus { get => _pauseStatus; set { if (_pauseStatus == value) return; _pauseStatus = value; Changed(); Changed(nameof(Header)); } }
    public string Header => MaintenancePresentation.Header(Computer, Address, Status, PauseStatus, CheckedUtc);
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class BulkWindowsUpdateWindow : Window
{
    readonly IReadOnlyList<ClientInfo> _clients;
    readonly HostSettings _settings;
    readonly BulkOperationRunner? _run;
    readonly ObservableCollection<BulkUpdateResult> _results = new();
    readonly TextBlock _status = Ui.StatusText();
    readonly CheckBox _optional = new() { Content = "Include optional/driver updates", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    readonly object _saveGate = new();
    bool _running;

    public BulkWindowsUpdateWindow(IReadOnlyList<ClientInfo> clients, HostSettings settings, Window owner, BulkOperationRunner? run = null)
    {
        _clients = clients;
        _settings = settings;
        _run = run;
        Owner = owner;
        Title = $"Windows Update — {clients.Count} computers";
        Width = 940;
        Height = 620;
        MinWidth = 680;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var client in clients)
        {
            var row = new BulkUpdateResult { Computer = client.Name, Address = client.Address };
            var saved = settings.WindowsUpdateSnapshots.LastOrDefault(snapshot =>
                snapshot.Address.Equals(client.Address, StringComparison.OrdinalIgnoreCase));
            if (saved is not null)
            {
                row.Status = saved.Status;
                row.CheckedUtc = saved.CheckedUtc;
                foreach (var item in saved.Updates) row.Items.Add(new BulkUpdateItem
                    { Title = item.Title, Details = MaintenancePresentation.SavedDetails(item.Details), StoredDetails = item.Details });
            }
            _results.Add(row);
        }

        var tree = new TreeView { ItemsSource = _results, Margin = new Thickness(0, 8, 0, 0) };
        var rootTemplate = new HierarchicalDataTemplate(typeof(BulkUpdateResult))
        {
            ItemsSource = new Binding(nameof(BulkUpdateResult.Items)),
        };
        var rootText = new FrameworkElementFactory(typeof(TextBlock));
        rootText.SetValue(UiTranslator.PreserveTextProperty, true);
        rootText.SetBinding(TextBlock.TextProperty, new Binding(nameof(BulkUpdateResult.Header)));
        rootText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        rootTemplate.VisualTree = rootText;
        var itemTemplate = new DataTemplate(typeof(BulkUpdateItem));
        var itemText = new FrameworkElementFactory(typeof(TextBlock));
        itemText.SetValue(UiTranslator.PreserveTextProperty, true);
        itemText.SetBinding(TextBlock.TextProperty, new Binding(nameof(BulkUpdateItem.Display)));
        itemText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        itemTemplate.VisualTree = itemText;
        rootTemplate.ItemTemplate = itemTemplate;
        tree.ItemTemplate = rootTemplate;
        tree.Resources.Add(new DataTemplateKey(typeof(BulkUpdateItem)), itemTemplate);

        var scan = Button("_Check selected clients", "↻", async (_, _) => await RunAsync(false));
        var install = Button("Update _all selected", "⬇", async (_, _) => await RunAsync(true));
        var pause = Button("_Pause updates...", "⏸", async (_, _) => await PauseAllAsync());
        var resume = Button("_Resume updates", "▶", async (_, _) => await SetPauseAsync(null));
        var refreshPause = Button("Refresh pause _status", "◷", async (_, _) => await RefreshPauseStatusesAsync());
        var version = Button("Windows _version...", "⬇", async (_, _) =>
        {
            if (_running || !await WindowsVersionDialog.ShowAsync(this, _clients, _settings)) return;
            await RefreshPauseStatusesAsync();
        });
        var close = Button("_Close", "✕", (_, _) => Close());
        close.IsCancel = true;
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(scan);
        buttons.Children.Add(install);
        buttons.Children.Add(pause);
        buttons.Children.Add(resume);
        buttons.Children.Add(refreshPause);
        buttons.Children.Add(version);
        buttons.Children.Add(_optional);
        ModuleUiPolicy.WatchVisibility(this, settings, BuiltInModules.Maintenance, scan, install, _optional, refreshPause);
        var (expandAll, collapseAll) = Ui.ExpandCollapseButtons(tree);
        buttons.Children.Add(expandAll);
        buttons.Children.Add(collapseAll);
        buttons.Children.Add(close);
        Ui.Spaced(buttons);
        var note = Ui.Explanation("Expand a computer, or use Expand all, to see the updates found. Pause status is read from each updated Client; pause and resume are machine-wide actions. The latest completed update check is saved immediately and restored after a new check, power action, or Host restart.", new Thickness(0, 0, 0, 4));
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        root.Children.Add(tree);
        Content = root;
        Loaded += async (_, _) => await RefreshPauseStatusesAsync();
        Closing += (_, _) => SaveSnapshots();
    }

    static Button Button(string text, string icon, RoutedEventHandler action)
    {
        var button = new Button { Content = text, Tag = icon, MinWidth = 145, ToolTip = text.Replace("_", "") };
        button.Click += action;
        return button;
    }

    async Task RunAsync(bool install)
    {
        if (_running || !BuiltInModules.Enabled(_settings, BuiltInModules.Maintenance)) return;
        if (install)
        {
            var review = Review(
                "Review Windows Update installation",
                [
                    new("Action", "Check, download, and install all offered updates"),
                    new("Optional/driver updates", _optional.IsChecked == true ? "Included" : "Excluded"),
                ],
                "Windows may require a restart after installation. This action cannot be undone.",
                "Install updates");
            if (new BulkOperationReviewWindow(this, review).Ask() != BulkOperationReviewDecision.Run) return;
        }
        SaveSnapshots();
        _running = true;
        try
        {
            _status.Text = install ? "Updating all selected clients..." : "Checking selected clients...";
            await ForEachAsync(install ? "Windows Update: install all updates" : "Windows Update: check for updates",
                (client, row) => RunOneAsync(client, row, install), row => row.Status.StartsWith("Failed", StringComparison.Ordinal) ? row.Status : null);
            _status.Text = install ? "Bulk Windows Update finished. Review each computer." : "Update checks finished. Expand each computer to review available updates.";
        }
        finally
        {
            _running = false;
            SaveSnapshots();
        }
    }

    async Task PauseAllAsync()
    {
        var dlg = new FormDialog("Pause Windows Update on selected computers", this)
            .Combo("period", "Pause period", WindowsUpdatePausePeriods.Choices, 1)
            .Note("This changes the machine-wide Windows Update pause on every selected computer.");
        if (!dlg.Ask("Pause all")) return;
        await SetPauseAsync(WindowsUpdatePausePeriods.ParseDays(dlg.S("period")));
    }

    async Task SetPauseAsync(int? days)
    {
        if (_running) return;
        var review = Review(
            days is null ? "Review Windows Update resume" : "Review Windows Update pause",
            [new("Action", days is null ? "Remove the update pause" : $"Pause updates for {days} day(s)")],
            "This changes the machine-wide Windows Update state on every selected computer.",
            days is null ? "Resume updates" : "Pause updates");
        if (new BulkOperationReviewWindow(this, review).Ask() != BulkOperationReviewDecision.Run) return;
        _running = true;
        try
        {
            _status.Text = days is null ? "Removing Windows Update pauses..." : "Pausing Windows Update...";
            await ForEachAsync(days is null ? "Windows Update: resume updates" : $"Windows Update: pause for {days} day(s)",
                (client, row) => SetPauseOneAsync(client, row, days),
                row => row.PauseStatus.StartsWith(Loc.T("Pause action failed: "), StringComparison.Ordinal) ? row.PauseStatus : null);
            _status.Text = days is null ? "Resume operation finished. Review each computer." : "Pause operation finished. Review each computer.";
        }
        finally { _running = false; }
    }

    /// <summary>
    /// Runs one step on every computer. With the main window's runner each computer is recorded in Operations and the activity log;
    /// <paramref name="failure"/> reads a row's outcome (the step records its errors in the row), so a failed row is a failed operation too.
    /// </summary>
    async Task ForEachAsync(string title, Func<ClientInfo, BulkUpdateResult, Task> one, Func<BulkUpdateResult, string?> failure)
    {
        BulkUpdateResult RowOf(ClientInfo client) => _results[_clients.ToList().IndexOf(client)];
        if (_run is null)
        {
            await Task.WhenAll(_clients.Select(client => one(client, RowOf(client))));
            return;
        }
        await _run(_clients.ToList(), title, async (client, _) =>
        {
            var row = RowOf(client);
            await one(client, row);
            if (failure(row) is { } error) throw new InvalidOperationException(error);
        }, 8, "", (_, _) => { });
    }

    BulkOperationReview Review(string heading, IReadOnlyList<BulkOperationReviewItem> items, string warning, string runLabel)
    {
        var target = OperationTargetSummary.Format(_clients);
        return new BulkOperationReview(
            heading,
            target.Heading + "\n" + target.Detail,
            items,
            "Step 3 of 5 — Review before starting. Run status and per-computer results remain visible in this window.",
            warning,
            runLabel);
    }

    async Task SetPauseOneAsync(ClientInfo client, BulkUpdateResult row, int? days)
    {
        try
        {
            row.PauseStatus = days is null ? Loc.T("Removing pause...") : Loc.T("Applying pause...");
            using var session = await ClientSession.OpenAsync(client, _settings);
            var (maintenance, machine) = ModuleRuns.OpenMaintenance(session, client);
            var answer = days is null
                ? await maintenance.ResumeAsync(machine, CancellationToken.None)
                : await maintenance.PauseAsync(machine, days.Value, CancellationToken.None);
            SetPauseStatus(row, JsonNode.Parse(answer)!.AsObject(), days is not null);
        }
        catch (Exception ex) { row.PauseStatus = Loc.T("Pause action failed: ") + ex.Message; }
    }

    async Task RefreshPauseStatusesAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            _status.Text = "Checking Windows Update pause status...";
            await Task.WhenAll(_clients.Select((client, index) => RefreshPauseStatusOneAsync(client, _results[index])));
            _status.Text = "Pause status check finished.";
        }
        finally { _running = false; }
    }

    async Task RefreshPauseStatusOneAsync(ClientInfo client, BulkUpdateResult row)
    {
        if (!client.SupportsCommand(Cmd.WindowsUpdateStatus))
        {
            row.PauseStatus = Loc.F($"Pause status requires Client protocol {Protocol.RequiredVersion(Cmd.WindowsUpdateStatus)}");
            return;
        }
        try
        {
            row.PauseStatus = Loc.T("Checking pause status...");
            using var session = await ClientSession.OpenAsync(client, _settings);
            var (maintenance, machine) = ModuleRuns.OpenMaintenance(session, client);
            SetPauseStatus(row, JsonNode.Parse(await maintenance.StatusAsync(machine, CancellationToken.None))!.AsObject(), false);
        }
        catch (Exception ex) { row.PauseStatus = Loc.T("Pause status unavailable: ") + ex.Message; }
    }

    void SetPauseStatus(BulkUpdateResult row, JsonObject data, bool pauseWasRequested)
    {
        if (!BuiltInModules.Enabled(_settings, BuiltInModules.Maintenance))
        {
            // Core recovery still reports the authoritative response, without invoking the inventory module.
            row.PauseStatus = data["paused"] is null ? Loc.T("Pause status not checked")
                : data["paused"]!.GetValue<bool>() ? Loc.T("Paused") : Loc.T("Running normally");
            return;
        }
        var projection = WindowsMaintenanceInventory.ProjectPause(data, pauseWasRequested, DateTimeOffset.UtcNow);
        var pause = MaintenancePresentation.Pause(projection);
        // Older Clients report only the pause; the Windows version and target come with it on newer ones.
        row.PauseStatus = WindowsRelease.Describe(data).Length > 0 ? $"{WindowsVersionDialog.Line(data)} · {pause}" : pause;
    }

    async Task RunOneAsync(ClientInfo client, BulkUpdateResult row, bool install)
    {
        if (!BuiltInModules.Enabled(_settings, BuiltInModules.Maintenance)) { row.Status = BuiltInModules.Disabled; return; }
        try
        {
            row.Status = "Checking Windows Update...";
            using var session = await ClientSession.OpenAsync(client, _settings);
            var (maintenance, machine) = ModuleRuns.OpenMaintenance(session, client);
            var data = JsonNode.Parse(await maintenance.ScanAsync(machine, _optional.IsChecked == true, CancellationToken.None))!.AsObject();
            var updates = WindowsMaintenanceInventory.ProjectScan(data).Updates;
            row.Items.Clear();
            foreach (var update in updates)
            {
                row.Items.Add(new BulkUpdateItem
                {
                    Title = update.Title,
                    Details = WindowsMaintenanceInventory.Details(update),
                    StoredDetails = MaintenancePresentation.Details(update, invariant: true),
                });
            }
            row.CheckedUtc = DateTime.UtcNow.ToString("O");
            row.Status = updates.Count == 0 ? "No updates available" : $"{updates.Count} update(s) available";
            SaveSnapshots();
            if (!install || updates.Count == 0) return;
            row.Status = $"Downloading and installing {updates.Count} update(s)...";
            var result = JsonNode.Parse(await maintenance.InstallAllAsync(machine, CancellationToken.None))!.AsObject();
            row.Status = $"Installed {result["selected"]?.GetValue<int>() ?? 0}; " +
                         (result["rebootRequired"]?.GetValue<bool>() == true ? "restart required" : "restart not required");
        }
        catch (Exception ex) { row.Status = "Failed: " + ex.Message; }
        finally { SaveSnapshots(); }
    }

    void SaveSnapshots()
    {
        lock (_saveGate)
        {
            foreach (var row in _results)
            {
                if (_running && (row.Status.StartsWith("Checking", StringComparison.Ordinal) ||
                                 row.Status.StartsWith("Downloading", StringComparison.Ordinal))) continue;
                var snapshot = _settings.WindowsUpdateSnapshots.FirstOrDefault(item =>
                    item.Address.Equals(row.Address, StringComparison.OrdinalIgnoreCase));
                if (snapshot is null)
                {
                    snapshot = new WindowsUpdateSnapshot { Address = row.Address };
                    _settings.WindowsUpdateSnapshots.Add(snapshot);
                }
                snapshot.Computer = row.Computer;
                snapshot.CheckedUtc = row.CheckedUtc;
                snapshot.Status = row.Status;
                snapshot.Updates = row.Items.Select(item => new WindowsUpdateSnapshotItem
                    { Title = item.Title, Details = item.StoredDetails ?? item.Details }).ToList();
            }
            _settings.Save();
        }
    }
}
