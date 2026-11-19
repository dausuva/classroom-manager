// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class TaskEntry
{
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";
    public string NextRun { get; init; } = "";
    public string LastRun { get; init; } = "";
    public string LastResult { get; init; } = "";
    public string RunAs { get; init; } = "";
    public string TaskToRun { get; init; } = "";
    public string Author { get; init; } = "";

    public static TaskEntry FromJson(JsonObject item) => new()
    {
        Name = item["name"]!.GetValue<string>(),
        Status = item["status"]!.GetValue<string>(),
        NextRun = item["nextRun"]!.GetValue<string>(),
        LastRun = item["lastRun"]!.GetValue<string>(),
        LastResult = item["lastResult"]!.GetValue<string>(),
        RunAs = item["runAs"]!.GetValue<string>(),
        TaskToRun = item["taskToRun"]!.GetValue<string>(),
        Author = item["author"]?.GetValue<string>() ?? "",
    };
}

/// <summary>Lists Scheduled Tasks on one computer and lets you enable/disable, run now, or delete one.</summary>
public sealed class TasksWindow : Window
{
    ClientInfo _client;
    readonly HostSettings _settings;
    readonly TextBox _filter = new() { Width = 220, Margin = new Thickness(6, 0, 0, 0) };
    readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    readonly TextBlock _status = Ui.StatusText();
    List<TaskEntry> _all = new();
    int _loadVersion;

    public TasksWindow(ClientInfo client, HostSettings settings, Window owner)
    {
        _client = client;
        _settings = settings;
        Owner = owner;
        Title = $"Scheduled tasks - {client.Name}";
        Width = 900;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var savedFilter = _settings.FilterState(nameof(TasksWindow));
        _filter.Text = savedFilter.Text;

        var grid = new GridView();
        grid.Columns.Add(Col("Task", "Name", 320));
        grid.Columns.Add(Col("Status", "Status", 90));
        grid.Columns.Add(Col("Runs as", "RunAs", 130));
        grid.Columns.Add(Col("Next run", "NextRun", 130));
        grid.Columns.Add(Col("Last result", "LastResult", 80));
        _list.View = grid;

        var refresh = new Button { Content = "_Refresh", Margin = new Thickness(6, 0, 0, 0) };
        refresh.Click += async (_, _) => await LoadAsync();
        _filter.TextChanged += (_, _) => ApplyFilter();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in ComputerPicker.ForRow(new ComputerPicker(client, SwitchTo))) top.Children.Add(item);
        top.Children.Add(Ui.Label("_Filter:", _filter, new Thickness(12, 0, 6, 0)));
        top.Children.Add(_filter);
        top.Children.Add(refresh);

        var enable = new Button { Content = "_Enable" };
        enable.Click += async (_, _) => await SetEnabledAsync(true);
        var disable = new Button { Content = "_Disable", Margin = new Thickness(6, 0, 0, 0) };
        disable.Click += async (_, _) => await SetEnabledAsync(false);
        var runNow = new Button { Content = "_Run now", Margin = new Thickness(6, 0, 0, 0) };
        runNow.Click += async (_, _) => await RunNowAsync();
        var delete = new Button { Content = "De_lete...", Margin = new Thickness(6, 0, 0, 0) };
        delete.Click += async (_, _) => await DeleteAsync();

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(enable);
        bottom.Children.Add(disable);
        bottom.Children.Add(runNow);
        bottom.Children.Add(delete);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_status);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, _) =>
        {
            savedFilter.Text = _filter.Text;
            _settings.Save();
        };
        Ui.RefreshOnF5(this, LoadAsync);
    }

    static GridViewColumn Col(string header, string path, double width) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new Binding(path) };

    /// <summary>The computer was changed in the picker: show it, with an empty list until its data arrives.</summary>
    async void SwitchTo(ClientInfo computer)
    {
        _client = computer;
        Title = $"Scheduled tasks - {computer.Name}";
        _all = new();
        ApplyFilter();
        _status.Text = ComputerPicker.NowShowing(computer);
        await LoadAsync();
    }

    async Task LoadAsync()
    {
        var loadVersion = ++_loadVersion;
        try
        {
            _status.Text = "Loading...";
            using var s = await ClientSession.OpenAsync(_client, _settings);
            var data = await s.CallAsync(Cmd.TasksList);
            if (loadVersion != _loadVersion) return;
            _all = data["tasks"]!.AsArray().Select(t => TaskEntry.FromJson(t!.AsObject()))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            TaskSnapshotCache.Set(_client, _all);
            ApplyFilter();
        }
        catch (Exception ex) { if (loadVersion == _loadVersion) _status.Text = "Error: " + ex.Message; }
    }

    void ApplyFilter()
    {
        var f = _filter.Text.Trim();
        var shown = _all.Where(t => f.Length == 0 ||
            t.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
            t.TaskToRun.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        Ui.ReplaceItemsPreservingSelection(_list, shown, t => t.Name);
        _status.Text = $"{shown.Count} task(s)";
    }

    List<TaskEntry> SelectedTasks() => _list.SelectedItems.Cast<TaskEntry>().ToList();

    async Task SetEnabledAsync(bool enabled)
    {
        var items = SelectedTasks();
        if (items.Count == 0) return;
        var failures = new List<string>();
        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            foreach (var t in items)
            {
                try { await s.CallAsync(Cmd.TasksSetEnabled, new JsonObject { ["name"] = t.Name, ["enabled"] = enabled }); }
                catch (RemoteException ex) { failures.Add($"{t.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { failures.Add(ex.Message); }
        TaskSnapshotCache.Invalidate([_client]);
        await LoadAsync();
        if (failures.Count > 0) MessageBox.Show(this, string.Join("\n\n", failures), "Some tasks could not be changed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    async Task RunNowAsync()
    {
        var items = SelectedTasks();
        if (items.Count == 0) return;
        var failures = new List<string>();
        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            foreach (var t in items)
            {
                try { await s.CallAsync(Cmd.TasksRun, new JsonObject { ["name"] = t.Name }); }
                catch (RemoteException ex) { failures.Add($"{t.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { failures.Add(ex.Message); }
        TaskSnapshotCache.Invalidate([_client]);
        if (failures.Count > 0) MessageBox.Show(this, string.Join("\n\n", failures), "Some tasks could not be run", MessageBoxButton.OK, MessageBoxImage.Warning);
        else _status.Text = $"Ran {items.Count} task(s).";
    }

    async Task DeleteAsync()
    {
        var items = SelectedTasks();
        if (items.Count == 0) return;
        var names = string.Join("\n", items.Take(8).Select(i => i.Name)) + (items.Count > 8 ? "\n..." : "");
        if (MessageBox.Show(this, $"Delete {items.Count} scheduled task(s) from {_client.Name}?\n\n{names}",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var failures = new List<string>();
        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            foreach (var t in items)
            {
                try { await s.CallAsync(Cmd.TasksDelete, new JsonObject { ["name"] = t.Name }); }
                catch (RemoteException ex) { failures.Add($"{t.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { failures.Add(ex.Message); }
        TaskSnapshotCache.Invalidate([_client]);
        await LoadAsync();
        if (failures.Count > 0) MessageBox.Show(this, string.Join("\n\n", failures), "Some tasks could not be deleted", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
