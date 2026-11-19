// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Snapshot comparison of running processes across selected computers.</summary>
public sealed class BulkProcessesWindow : Window
{
    const string UserView = "Signed-in user session";
    const string MachineView = "Elevated machine view";
    const string AllProcesses = "All processes";
    const string DifferencesOnly = "Differences only";
    const string VersionDifferences = "Version differences";
    const string MissingProcesses = "Not running on some PCs";
    const string WindowedApps = "Windowed apps";

    readonly IReadOnlyList<ClientInfo> _clients;
    readonly HostSettings _settings;
    readonly Action<string>? _findProgram;
    readonly ComboBox _source = new() { ItemsSource = new[] { UserView, MachineView }, SelectedIndex = 0, Width = 185 };
    readonly ComboBox _owner = new() { ItemsSource = new[] { "User", "System", "Unknown", "All owners" }, SelectedIndex = 0, Width = 115 };
    readonly ComboBox _comparison = new() { ItemsSource = new[] { AllProcesses, DifferencesOnly, VersionDifferences, MissingProcesses, WindowedApps }, SelectedIndex = 0, Width = 185 };
    readonly TextBox _filter = new() { Width = 180 };
    readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        SelectionMode = DataGridSelectionMode.Extended,
        SelectionUnit = DataGridSelectionUnit.FullRow,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
    };
    readonly TextBlock _status = Ui.StatusText();
    readonly Button _refresh;
    readonly Button _endTasks;
    Dictionary<ClientInfo, IReadOnlyList<ProcessEntry>> _inventories = new();
    List<string> _errors = [];
    List<ComparedProcess> _all = [];
    bool _running;
    int _loadVersion;

    public BulkProcessesWindow(IReadOnlyList<ClientInfo> clients, HostSettings settings, Window owner, Action<string>? findProgram = null)
    {
        _clients = clients;
        _findProgram = findProgram;
        _settings = settings;
        Owner = owner;
        Title = $"Processes comparison - {clients.Count} computers";
        Width = 1320;
        Height = 700;
        MinWidth = 900;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var saved = settings.FilterState(nameof(BulkProcessesWindow));
        _filter.Text = saved.Text;
        SelectSaved(_source, saved.Selection("View", UserView));
        SelectSaved(_owner, saved.Selection("Owner", "User"));
        SelectSaved(_comparison, saved.Selection("Comparison", AllProcesses));

        AddColumn("Process", nameof(ComparedProcess.Name), 170);
        AddColumn("Version(s)", nameof(ComparedProcess.Versions), 135);
        AddColumn("Running", nameof(ComparedProcess.Running), 105);
        AddColumn("Instances", nameof(ComparedProcess.Instances), 70);
        AddColumn("Memory", nameof(ComparedProcess.Memory), 90);
        AddColumn("Owner(s)", nameof(ComparedProcess.Owners), 150);
        AddColumn("Type(s)", nameof(ComparedProcess.Types), 190);
        AddColumn("Computer details", nameof(ComparedProcess.ComputerDetails), 300);
        AddColumn("Not running on", nameof(ComparedProcess.MissingComputers), 170);
        AddColumn("Window titles", nameof(ComparedProcess.WindowTitles), 300);
        AddColumn("Executable path(s)", nameof(ComparedProcess.Paths), 300);
        AddColumn("Comparison", nameof(ComparedProcess.Comparison), 220);
        TableColumns.Apply(_grid, saved.HiddenColumns);

        _refresh = ActionButton("_Recheck", "\u21BB", "Reload the current process snapshot from every selected computer.");
        _refresh.Click += async (_, _) => await LoadAsync();
        // "o" is already the Owner filter's access key in this window.
        var columns = ActionButton("Colum_ns...", "\u2630", "Columns \u2014 choose which comparison columns are visible.");
        columns.Click += (_, _) =>
        {
            if (TableColumns.Choose(this, "Processes comparison columns", _grid, saved)) _settings.Save();
        };
        _endTasks = ActionButton("_End selected non-system processes", "\u25A0", "End selected verified user or unknown-owner process instances across checked computers.");
        _endTasks.Click += async (_, _) => await EndTasksAsync();
        var close = ActionButton("_Close", "\u2715", "Close the process comparison.");
        close.IsCancel = true;
        close.Click += (_, _) => Close();

        _source.SelectionChanged += async (_, _) => { if (IsLoaded) await LoadAsync(); };
        _owner.SelectionChanged += (_, _) => RebuildComparison();
        _comparison.SelectionChanged += (_, _) => ApplyFilter();
        _filter.TextChanged += (_, _) => ApplyFilter();

        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        controls.Children.Add(Ui.Label("_View:", _source));
        controls.Children.Add(_source);
        controls.Children.Add(Ui.Label("_Owner:", _owner, new Thickness(12, 0, 6, 0)));
        controls.Children.Add(_owner);
        controls.Children.Add(Ui.Label("_Show:", _comparison, new Thickness(12, 0, 6, 0)));
        controls.Children.Add(_comparison);
        controls.Children.Add(Ui.Label("_Filter:", _filter, new Thickness(12, 0, 6, 0)));
        controls.Children.Add(_filter);
        controls.Children.Add(_refresh);
        controls.Children.Add(columns);
        Ui.Spaced(controls);

        var note = Ui.Explanation(
            "Recommendation: inspect and end processes on one computer at a time whenever possible. " +
            "This multi-computer view is a point-in-time comparison; process IDs and application state change quickly. " +
            "Batch ending is limited to verified non-system processes from updated clients. System processes are skipped; unknown-owner processes remain eligible when their identity and start time can be verified.",
            new Thickness(0, 0, 0, 8));
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        if (findProgram is not null)
        {
            var find = ActionButton("_Find program to uninstall...", "\uD83D\uDD0D", "Find program to uninstall — search the installed programs of these computers by name, starting from the selected process.");
            find.Click += (_, _) => FindProgram();
            actions.Children.Add(find);
            ModuleUiPolicy.WatchVisibility(this, settings, BuiltInModules.Software, find);
        }
        actions.Children.Add(_endTasks);
        actions.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(controls, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(controls);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(actions);
        root.Children.Add(_grid);
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, _) =>
        {
            saved.Text = _filter.Text;
            saved.Selections["View"] = _source.SelectedItem as string ?? UserView;
            saved.Selections["Owner"] = _owner.SelectedItem as string ?? "User";
            saved.Selections["Comparison"] = _comparison.SelectedItem as string ?? AllProcesses;
            _settings.Save();
        };
        Ui.RefreshOnF5(this, LoadAsync);
    }

    string Level => _source.SelectedIndex == 0 ? Levels.User : Levels.System;

    void FindProgram()
    {
        var suggested = _grid.SelectedItems.Count == 1 && _grid.SelectedItem is ComparedProcess row ? ProgramLaunch.SearchName(row.Name) : "";
        var name = ProgramLaunch.AskSearchName(this, suggested);
        if (name.Length > 0) _findProgram?.Invoke(name);
    }

    static void SelectSaved(ComboBox box, string value)
    {
        if (box.Items.Contains(value)) box.SelectedItem = value;
    }

    void AddColumn(string header, string path, double width) =>
        _grid.Columns.Add(new DataGridTextColumn { Header = header, Width = width, Binding = new Binding(path) });

    static Button ActionButton(string content, string icon, string tooltip) => new()
    {
        Content = content,
        Tag = icon,
        ToolTip = tooltip,
        MinWidth = 110,
    };

    async Task LoadAsync()
    {
        if (_running) return;
        var version = ++_loadVersion;
        SetRunning(true);
        try
        {
            var completed = 0;
            _status.Text = $"Checking running processes on {_clients.Count} computers...";
            using var gate = new SemaphoreSlim(8);
            var results = await Task.WhenAll(_clients.Select(async client =>
            {
                await gate.WaitAsync();
                try { return await LoadOneAsync(client); }
                finally
                {
                    gate.Release();
                    var count = Interlocked.Increment(ref completed);
                    await Dispatcher.InvokeAsync(() => _status.Text = $"Checked {count} of {_clients.Count} computers...");
                }
            }));
            if (version != _loadVersion) return;

            _inventories = results.Where(result => result.Error.Length == 0)
                .ToDictionary(result => result.Client, result => (IReadOnlyList<ProcessEntry>)result.Processes);
            _errors = results.Where(result => result.Error.Length > 0)
                .Select(result => $"{result.Client.Name}: {result.Error}").ToList();
            RebuildComparison();
            SetSummaryStatus(_grid.Items.Count);
        }
        finally { SetRunning(false); }
    }

    async Task<ProcessInventoryResult> LoadOneAsync(ClientInfo client)
    {
        try
        {
            if (client.ExclusiveOperation.Length > 0) return new(client, [], client.ExclusiveOperation);
            using var session = await ClientSession.OpenAsync(client, _settings);
            var data = await session.CallAtAsync(Level, Cmd.ProcList);
            var processes = data["processes"]!.AsArray()
                .Select(item => ProcessEntry.FromJson(item!.AsObject())).ToList();
            return new(client, processes, "");
        }
        catch (Exception ex) { return new(client, [], ex.Message); }
    }

    void RebuildComparison()
    {
        var owner = _owner.SelectedItem as string ?? "User";
        var filtered = _inventories.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ProcessEntry>)pair.Value
                .Where(process => owner == "All owners" || process.Role.Equals(owner, StringComparison.OrdinalIgnoreCase))
                .ToList());
        _all = ProcessInventoryComparison.Build(_clients, filtered);
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var text = _filter.Text.Trim();
        var mode = _comparison.SelectedItem as string ?? AllProcesses;
        var shown = _all.Where(item =>
            FilterMatch.Matches(text, item.Name, item.Paths, item.WindowTitles, item.Owners) &&
            (mode == AllProcesses ||
             mode == DifferencesOnly && item.HasDifference ||
             mode == VersionDifferences && item.HasVersionDifference ||
             mode == MissingProcesses && item.IsMissingFromSome ||
             mode == WindowedApps && item.HasVisibleWindow)).ToList();
        TableSorting.SetItemsPreservingSort(_grid, shown);
        if (!_running) SetSummaryStatus(shown.Count);
    }

    void SetSummaryStatus(int shown) =>
        _status.Text = $"Showing {shown} of {_all.Count} process groups across {_inventories.Count} of {_clients.Count} computers." +
            (_errors.Count == 0 ? "" : " Unavailable: " + string.Join("; ", _errors));

    async Task EndTasksAsync()
    {
        if (_running) return;
        var selected = _grid.SelectedItems.Cast<ComparedProcess>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select one or more process rows first.", "Batch end task", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var entries = selected.SelectMany(item => item.Entries)
            .DistinctBy(entry => $"{entry.Client.Address}\0{entry.Process.Pid}\0{entry.Process.StartUtcTicks}", StringComparer.OrdinalIgnoreCase)
            .ToList();
        var runnable = entries.Where(entry => ProcessInventoryComparison.SafeToBatchEnd(entry.Process)).ToList();
        var skipped = entries.Count - runnable.Count;
        if (runnable.Count == 0)
        {
            MessageBox.Show(this,
                "No selected instances are eligible for batch termination. Batch mode only ends verified user or unknown-owner processes from clients that report process start times. " +
                "Open Processes for one computer to inspect system, protected, or older-client entries.",
                "Batch end task", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var computerCount = runnable.Select(entry => entry.Client).Distinct().Count();
        var names = string.Join("\n", selected.Take(10).Select(item => $"• {item.Name} ({item.Instances} visible instance(s))")) +
                    (selected.Count > 10 ? "\n• ..." : "");
        if (MessageBox.Show(this,
                $"End {runnable.Count} verified non-system process instance(s) across {computerCount} computer(s)?\n\n{names}\n\n" +
                $"{skipped} system, protected, or unverifiable instance(s) will be skipped.\n\n" +
                "Checking and ending processes one computer at a time is safer. Continue with the batch action?",
                "Confirm batch end task", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        SetRunning(true);
        var failures = new ConcurrentBag<string>();
        var completed = 0;
        try
        {
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(runnable.GroupBy(entry => entry.Client).Select(async group =>
            {
                await gate.WaitAsync();
                var client = group.Key;
                Interlocked.Increment(ref client.BusyCount);
                try
                {
                    if (client.ExclusiveOperation.Length > 0)
                    {
                        failures.Add($"{client.Name}: {client.ExclusiveOperation}");
                        return;
                    }
                    using var session = await ClientSession.OpenAsync(client, _settings);
                    foreach (var entry in group)
                    {
                        try
                        {
                            await session.CallAsync(Cmd.ProcKill, new JsonObject
                            {
                                ["pid"] = entry.Process.Pid,
                                ["expectedName"] = entry.Process.Name,
                                ["expectedStartUtcTicks"] = entry.Process.StartUtcTicks,
                            });
                        }
                        catch (RemoteException ex) when (ProcessKillResults.AlreadyGone(ex.Message)) { }
                        catch (Exception ex)
                        {
                            failures.Add($"{client.Name} - {entry.Process.Name} (pid {entry.Process.Pid}): {ex.Message}");
                        }
                        finally
                        {
                            var count = Interlocked.Increment(ref completed);
                            await Dispatcher.InvokeAsync(() =>
                                _status.Text = $"Completed {count} of {runnable.Count} end-task operations...");
                        }
                    }
                }
                catch (Exception ex) { failures.Add($"{client.Name}: {ex.Message}"); }
                finally
                {
                    Interlocked.Decrement(ref client.BusyCount);
                    gate.Release();
                }
            }));
        }
        finally { SetRunning(false); }

        await LoadAsync();
        if (!failures.IsEmpty)
            MessageBox.Show(this, string.Join("\n\n", failures.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                "Some processes could not be ended", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void SetRunning(bool value)
    {
        _running = value;
        _refresh.IsEnabled = !value;
        _endTasks.IsEnabled = !value;
        _source.IsEnabled = !value;
    }

    sealed record ProcessInventoryResult(ClientInfo Client, List<ProcessEntry> Processes, string Error);
}
