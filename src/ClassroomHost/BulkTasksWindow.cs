// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Cached tree of Scheduled Tasks from several computers, with per-computer management.</summary>
public sealed class BulkTasksWindow : Window
{
    readonly IReadOnlyList<ClientInfo> _clients;
    readonly HostSettings _settings;
    readonly TextBox _filter = new() { Width = 260 };
    readonly TreeView _tree = new();
    readonly TextBlock _status = Ui.StatusText();
    IReadOnlyList<TaskInventoryResult> _results = [];
    string _resultSource = "Saved scan";
    int _loadVersion;

    public BulkTasksWindow(IReadOnlyList<ClientInfo> clients, HostSettings settings, Window owner)
    {
        _clients = clients;
        _settings = settings;
        Owner = owner;
        Title = $"Scheduled tasks - {clients.Count} computers";
        Width = 1080;
        Height = 660;
        MinWidth = 760;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var saved = settings.FilterState(nameof(BulkTasksWindow));
        _filter.Text = saved.Text;
        _filter.TextChanged += (_, _) => RenderFiltered();
        var refresh = Button("_Recheck", "\u21BB", "Reload Scheduled Tasks from every selected computer.");
        refresh.Click += async (_, _) => await LoadAsync();
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(Ui.Label("_Filter:", _filter));
        top.Children.Add(_filter);
        top.Children.Add(refresh);
        var (expandAll, collapseAll) = Ui.ExpandCollapseButtons(_tree);
        top.Children.Add(expandAll);
        top.Children.Add(collapseAll);
        Ui.Spaced(top);

        var note = Ui.Explanation(
            "Expand a computer to review its last detected non-Microsoft Scheduled Tasks. " +
            "Select a computer to open its full manager, or select one task to enable, disable, run, or delete it. Recheck replaces the cached scan.",
            new Thickness(0, 0, 0, 8));
        var manage = Button("_Manage computer...", "\u2699", "Open the full Scheduled Tasks manager for the selected computer.");
        manage.Click += (_, _) => OpenManager();
        var enable = Button("_Enable", "\u2713", "Enable the selected Scheduled Task.");
        enable.Click += async (_, _) => await SetEnabledAsync(true);
        var disable = Button("_Disable", "\u2012", "Disable the selected Scheduled Task.");
        disable.Click += async (_, _) => await SetEnabledAsync(false);
        var run = Button("_Run now", "\u25B6", "Run the selected Scheduled Task now.");
        run.Click += async (_, _) => await RunNowAsync();
        var delete = Button("De_lete...", "\uD83D\uDDD1", "Permanently delete the selected Scheduled Task.");
        delete.Click += async (_, _) => await DeleteAsync();
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(manage);
        actions.Children.Add(enable);
        actions.Children.Add(disable);
        actions.Children.Add(run);
        actions.Children.Add(delete);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(actions);
        root.Children.Add(_tree);
        Content = root;

        Loaded += async (_, _) => await ShowCachedOrLoadAsync();
        Closing += (_, _) =>
        {
            saved.Text = _filter.Text;
            _settings.Save();
        };
        Ui.RefreshOnF5(this, LoadAsync);
    }

    static Button Button(string content, string icon, string tooltip) => new()
    {
        Content = content,
        Tag = icon,
        ToolTip = tooltip,
        MinWidth = 88,
    };

    async Task ShowCachedOrLoadAsync()
    {
        if (!IsLoaded) return;
        var cached = _clients.Select(client => TaskSnapshotCache.TryGet(client, out var tasks)
            ? new TaskInventoryResult(client, tasks, "")
            : null).ToList();
        if (cached.All(result => result is not null))
        {
            Render(cached.Cast<TaskInventoryResult>().ToArray(), "Saved scan");
            return;
        }
        await LoadAsync();
    }

    async Task LoadAsync()
    {
        if (!IsLoaded) return;
        var version = ++_loadVersion;
        _status.Text = $"Loading Scheduled Tasks from {_clients.Count} computers...";
        using var gate = new SemaphoreSlim(8);
        var results = await Task.WhenAll(_clients.Select(async client =>
        {
            await gate.WaitAsync();
            try { return await LoadOneAsync(client); }
            finally { gate.Release(); }
        }));
        if (version != _loadVersion) return;
        Render(results, "Current scan");
    }

    async Task<TaskInventoryResult> LoadOneAsync(ClientInfo client)
    {
        try
        {
            if (client.ExclusiveOperation.Length > 0) return new(client, [], client.ExclusiveOperation);
            using var session = await ClientSession.OpenAsync(client, _settings);
            var data = await session.CallAsync(Cmd.TasksList);
            var tasks = data["tasks"]!.AsArray().Select(node => TaskEntry.FromJson(node!.AsObject()))
                .OrderBy(task => task.Name, StringComparer.OrdinalIgnoreCase).ToList();
            TaskSnapshotCache.Set(client, tasks);
            return new(client, tasks, "");
        }
        catch (Exception ex) { return new(client, [], ex.Message); }
    }

    void Render(IReadOnlyList<TaskInventoryResult> results, string source)
    {
        _results = results;
        _resultSource = source;
        RenderFiltered();
    }

    void RenderFiltered()
    {
        _tree.Items.Clear();
        var filter = _filter.Text.Trim();
        var total = _results.Sum(result => result.Tasks.Count);
        var shown = 0;
        foreach (var result in _results.OrderBy(item => item.Client.Name, StringComparer.OrdinalIgnoreCase))
        {
            var tasks = result.Tasks.Where(task => Matches(task, filter)).ToList();
            if (filter.Length > 0 && result.Error.Length == 0 && tasks.Count == 0) continue;
            var root = new TreeViewItem
            {
                Header = result.Error.Length > 0
                    ? $"{result.Client.Name} ({result.Client.Address}) - failed"
                    : $"{result.Client.Name} ({result.Client.Address}) - {tasks.Count}" +
                      (filter.Length > 0 ? $" matching of {result.Tasks.Count}" : " tasks"),
                ToolTip = result.Error.Length > 0 ? result.Error : null,
                IsExpanded = _results.Count <= 5 || filter.Length > 0,
                Tag = result.Client,
            };
            if (result.Error.Length > 0)
                root.Items.Add(new TreeViewItem { Header = "Error: " + result.Error });
            else
            {
                foreach (var task in tasks)
                {
                    var schedule = task.NextRun.Length > 0 ? $" · next {task.NextRun}" : "";
                    root.Items.Add(new TreeViewItem
                    {
                        Header = $"{task.Name} - {task.Status}{schedule}",
                        ToolTip = $"Runs as: {Value(task.RunAs)}\nCommand: {Value(task.TaskToRun)}\n" +
                                  $"Last run: {Value(task.LastRun)}\nLast result: {Value(task.LastResult)}\nAuthor: {Value(task.Author)}",
                        Tag = new TaskSelection(result.Client, task),
                    });
                }
                shown += tasks.Count;
            }
            _tree.Items.Add(root);
        }
        _status.Text = filter.Length == 0
            ? $"{_resultSource}: {total} tasks across {_clients.Count} computers."
            : $"{_resultSource}: showing {shown} of {total} tasks across {_clients.Count} computers.";
    }

    public static bool Matches(TaskEntry task, string filter) => filter.Length == 0 ||
        task.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        task.Status.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        task.RunAs.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        task.TaskToRun.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        task.Author.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        task.LastResult.Contains(filter, StringComparison.OrdinalIgnoreCase);

    static string Value(string value) => value.Length == 0 ? "(not reported)" : value;

    ClientInfo? SelectedClient() => (_tree.SelectedItem as TreeViewItem)?.Tag switch
    {
        ClientInfo client => client,
        TaskSelection selection => selection.Client,
        _ => null,
    };

    TaskSelection? SelectedTask()
    {
        if ((_tree.SelectedItem as TreeViewItem)?.Tag is TaskSelection selection) return selection;
        MessageBox.Show(this, "Select one Scheduled Task first.", "Scheduled Tasks", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    void OpenManager()
    {
        var client = SelectedClient();
        if (client is null)
        {
            MessageBox.Show(this, "Select a computer or one of its Scheduled Tasks first.");
            return;
        }
        new TasksWindow(client, _settings, this).ShowDialog();
        TaskSnapshotCache.Invalidate([client]);
        _ = LoadAsync();
    }

    async Task SetEnabledAsync(bool enabled)
    {
        var selection = SelectedTask();
        if (selection is null) return;
        await RunTaskActionAsync(selection, Cmd.TasksSetEnabled,
            new JsonObject { ["name"] = selection.Task.Name, ["enabled"] = enabled },
            enabled ? "enable" : "disable");
    }

    async Task RunNowAsync()
    {
        var selection = SelectedTask();
        if (selection is null) return;
        await RunTaskActionAsync(selection, Cmd.TasksRun,
            new JsonObject { ["name"] = selection.Task.Name }, "run");
    }

    async Task DeleteAsync()
    {
        var selection = SelectedTask();
        if (selection is null) return;
        if (MessageBox.Show(this,
                $"Permanently delete {selection.Task.Name} from {selection.Client.Name}?",
                "Confirm Scheduled Task deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunTaskActionAsync(selection, Cmd.TasksDelete,
            new JsonObject { ["name"] = selection.Task.Name }, "delete");
    }

    async Task RunTaskActionAsync(TaskSelection selection, string command, JsonObject args, string verb)
    {
        try
        {
            if (selection.Client.ExclusiveOperation.Length > 0)
                throw new InvalidOperationException(selection.Client.ExclusiveOperation);
            using var session = await ClientSession.OpenAsync(selection.Client, _settings);
            await session.CallAsync(command, args);
            TaskSnapshotCache.Invalidate([selection.Client]);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not {verb} {selection.Task.Name}:\n\n{ex.Message}",
                "Scheduled Tasks", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    sealed record TaskInventoryResult(ClientInfo Client, List<TaskEntry> Tasks, string Error);
    sealed record TaskSelection(ClientInfo Client, TaskEntry Task);
}
