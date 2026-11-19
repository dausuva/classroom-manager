// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class ProcessEntry
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public int SessionId { get; init; }
    public string Title { get; init; } = "";
    public string Path { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>The name the program is known by ("Microsoft Edge" for msedge); empty from a Client that does not send it.</summary>
    public string Description { get; init; } = "";
    public long StartUtcTicks { get; init; }
    public double MemoryMb { get; init; }
    public string Owner { get; init; } = "";
    public string Role { get; init; } = "";
    public string Kind { get; init; } = "";

    public static ProcessEntry FromJson(JsonObject item) => new()
    {
        Pid = item["pid"]!.GetValue<int>(),
        Name = item["name"]!.GetValue<string>(),
        SessionId = item["sessionId"]!.GetValue<int>(),
        Title = item["title"]!.GetValue<string>(),
        Path = item["path"]?.GetValue<string>() ?? "",
        Version = item["version"]?.GetValue<string>() ?? "",
        Description = item["description"]?.GetValue<string>() ?? "",
        StartUtcTicks = item["startUtcTicks"]?.GetValue<long>() ?? 0,
        MemoryMb = item["memoryMb"]!.GetValue<double>(),
        Owner = item["owner"]?.GetValue<string>() ?? "",
        Role = item["role"]?.GetValue<string>() ?? "Unknown",
        Kind = item["kind"]?.GetValue<string>() ?? "Background",
    };
}

/// <summary>Lists running processes on one computer and ends them by pid.</summary>
public sealed class ProcessesWindow : Window
{
    sealed record ProcessColumn(string Header, string Path, double Width);
    static readonly ProcessColumn[] Columns =
    [
        new("PID", nameof(ProcessEntry.Pid), 60),
        new("Name", nameof(ProcessEntry.Name), 190),
        new("Description", nameof(ProcessEntry.Description), 200),
        new("Type", nameof(ProcessEntry.Kind), 145),
        new("Run by", nameof(ProcessEntry.Owner), 165),
        new("Session", nameof(ProcessEntry.SessionId), 70),
        new("Window title", nameof(ProcessEntry.Title), 300),
        new("Executable", nameof(ProcessEntry.Path), 300),
        new("Version", nameof(ProcessEntry.Version), 120),
        new("Memory (MB)", nameof(ProcessEntry.MemoryMb), 90),
    ];

    ClientInfo _client;
    readonly HostSettings _settings;
    readonly TextBox _filter = new() { Width = 200, Margin = new Thickness(6, 0, 0, 0) };
    readonly ComboBox _source = new() { ItemsSource = new[] { "Signed-in user session", "Elevated machine view" }, SelectedIndex = 0, Width = 180 };
    readonly ComboBox _owner = new() { ItemsSource = new[] { "User", "System", "Unknown", "All owners" }, SelectedIndex = 0, Width = 115 };
    readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    readonly TextBlock _status = Ui.StatusText();
    List<ProcessEntry> _all = new();
    string _sourceLabel = "remote computer";
    int _loadVersion;

    public ProcessesWindow(ClientInfo client, HostSettings settings, Window owner)
    {
        _client = client;
        _settings = settings;
        Owner = owner;
        Title = $"Processes - {client.Name}";
        Width = 1260;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var savedFilter = _settings.FilterState(nameof(ProcessesWindow));
        _filter.Text = savedFilter.Text;
        var savedView = savedFilter.Selection("View", "Signed-in user session");
        var savedOwner = savedFilter.Selection("Owner", "User");
        if (_source.Items.Contains(savedView)) _source.SelectedItem = savedView;
        if (_owner.Items.Contains(savedOwner)) _owner.SelectedItem = savedOwner;

        RebuildColumns();

        var refresh = new Button { Content = "_Refresh", ToolTip = "Refresh (F5) — reload processes from the selected remote computer.", Margin = new Thickness(6, 0, 0, 0) };
        refresh.Click += async (_, _) => await LoadAsync();
        _filter.TextChanged += (_, _) => ApplyFilter();
        _owner.SelectionChanged += (_, _) => ApplyFilter();
        _source.SelectionChanged += async (_, _) => await LoadAsync();
        var columns = new Button { Content = "C_olumns...", ToolTip = "Choose visible process columns", Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(6, 0, 0, 0) };
        columns.Click += (_, _) => ChooseColumns();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in ComputerPicker.ForRow(new ComputerPicker(client, SwitchTo))) top.Children.Add(item);
        top.Children.Add(Ui.Label("_View:", _source, new Thickness(12, 0, 6, 0)));
        top.Children.Add(_source);
        top.Children.Add(Ui.Label("_Owner:", _owner, new Thickness(12, 0, 6, 0)));
        top.Children.Add(_owner);
        top.Children.Add(Ui.Label("_Filter:", _filter, new Thickness(12, 0, 6, 0)));
        top.Children.Add(_filter);
        top.Children.Add(refresh);
        top.Children.Add(columns);

        var endTask = new Button { Content = "_End task", ToolTip = "End task — terminate selected processes on the remote computer.", HorizontalAlignment = HorizontalAlignment.Right };
        endTask.Click += async (_, _) => await KillAsync();
        var findApp = new Button { Content = "_Find program to uninstall...", ToolTip = "Find program to uninstall — search the installed programs by name, starting from the selected process.", HorizontalAlignment = HorizontalAlignment.Right };
        findApp.Click += (_, _) => FindAppToUninstall();
        var copyPath = new Button { Content = "Copy _path", ToolTip = "Copy path — copy the executable path of one selected process." };
        copyPath.Click += (_, _) => CopyPath();
        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(copyPath, Dock.Right);
        DockPanel.SetDock(endTask, Dock.Right);
        DockPanel.SetDock(findApp, Dock.Right);
        bottom.Children.Add(copyPath);
        bottom.Children.Add(endTask);
        bottom.Children.Add(findApp);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_status);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        var note = Ui.Explanation("Windowed app means Windows reported a visible titled top-level window. Background includes processes without one. Windows does not provide a reliable supported mapping from notification-area icons back to every owning process.", new Thickness(0, 0, 0, 8));
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Insert(0, note);

        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, _) =>
        {
            savedFilter.Text = _filter.Text;
            savedFilter.Selections["View"] = _source.SelectedItem as string ?? "Signed-in user session";
            savedFilter.Selections["Owner"] = _owner.SelectedItem as string ?? "User";
            _settings.Save();
        };
        Ui.RefreshOnF5(this, LoadAsync);
    }

    /// <summary>The computer was changed in the picker: show it, with an empty list until its data arrives.</summary>
    async void SwitchTo(ClientInfo computer)
    {
        _client = computer;
        Title = $"Processes - {computer.Name}";
        _all = new();
        ApplyFilter();
        _status.Text = ComputerPicker.NowShowing(computer);
        await LoadAsync();
    }

    static GridViewColumn Col(string header, string path, double width) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new Binding(path) };

    void RebuildColumns()
    {
        var view = new GridView();
        foreach (var column in Columns.Where(column =>
                     !_settings.HiddenProcessColumns.Contains(column.Header, StringComparer.OrdinalIgnoreCase)))
            view.Columns.Add(Col(column.Header, column.Path, column.Width));
        _list.View = view;
    }

    void ChooseColumns()
    {
        var visible = Columns.Where(column =>
                !_settings.HiddenProcessColumns.Contains(column.Header, StringComparer.OrdinalIgnoreCase))
            .Select(column => column.Header).ToList();
        var dialog = new FormDialog("Process columns", this)
            .MultiSelect("columns", "Columns to show", Columns.Select(column => column.Header).ToList(), visible);
        if (!dialog.Ask("Save")) return;
        var selected = dialog.S("columns").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _settings.HiddenProcessColumns = Columns.Where(column => !selected.Contains(column.Header))
            .Select(column => column.Header).ToList();
        _settings.Save();
        RebuildColumns();
    }

    async Task LoadAsync()
    {
        var loadVersion = ++_loadVersion;
        try
        {
            _status.Text = "Loading...";
            using var s = await ClientSession.OpenAsync(_client, _settings);
            var data = await s.CallAtAsync(_source.SelectedIndex == 0 ? Levels.User : Levels.System, Cmd.ProcList);
            if (loadVersion != _loadVersion) return;
            var machine = data["machineName"]?.GetValue<string>() ?? _client.Name;
            _sourceLabel = $"{machine} ({_client.Address})";
            _all = data["processes"]!.AsArray().Select(p => ProcessEntry.FromJson(p!.AsObject()))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ApplyFilter();
        }
        catch (Exception ex) { if (loadVersion == _loadVersion) _status.Text = "Error: " + ex.Message; }
    }

    void FindAppToUninstall()
    {
        var items = _list.SelectedItems.Cast<ProcessEntry>().ToList();
        var selected = items.Count == 1 ? items[0] : null;
        var suggested = selected is null ? "" : ProgramLaunch.SearchName(selected.Name);
        var name = ProgramLaunch.AskSearchName(this, suggested);
        if (name.Length == 0) return;
        // The selected process's own path finds its entry most surely; a name typed instead is searched as text.
        if (selected is not null && name.Equals(suggested, StringComparison.OrdinalIgnoreCase))
            new ProgramsWindow(_client, _settings, this, selected.Path, selected.Name).Show();
        else
            new ProgramsWindow(_client, _settings, this, "", name).Show();
    }

    void CopyPath()
    {
        var selected = _list.SelectedItems.Cast<ProcessEntry>().ToList();
        if (selected.Count != 1 || selected[0].Path.Length == 0)
        {
            MessageBox.Show(this, "Select one process with an executable path first.", "Copy executable path");
            return;
        }
        Clipboard.SetText(selected[0].Path);
        _status.Text = "Executable path copied to the clipboard.";
    }

    void ApplyFilter()
    {
        var f = _filter.Text.Trim();
        var owner = _owner.SelectedItem as string ?? "User";
        // Every word must be found in the name, description, window title, owner, kind, file path or version, in any order.
        bool Wanted(ProcessEntry p) => FilterMatch.Matches(f, p.Name, p.Description, p.Title, p.Owner, p.Kind, p.Path, p.Version);
        var shown = _all.Where(p => (owner == "All owners" || p.Role.Equals(owner, StringComparison.OrdinalIgnoreCase)) && Wanted(p)).ToList();
        // Observable, so an ended process can leave the list without a reload (see KillAsync).
        Ui.ReplaceItemsPreservingSelection(_list, new System.Collections.ObjectModel.ObservableCollection<ProcessEntry>(shown), p => $"{p.Pid}\0{p.SessionId}\0{p.Name}");
        var elsewhere = shown.Count == 0 && f.Length > 0 && owner != "All owners" ? _all.Count(Wanted) : 0;
        _status.Text = elsewhere > 0
            ? $"No {owner} process matches \"{f}\", but {elsewhere} process(es) of other owners do. Set Owner to All owners to see them."
            : $"{shown.Count} process(es) loaded from {_sourceLabel}";
    }

    async Task KillAsync()
    {
        var items = _list.SelectedItems.Cast<ProcessEntry>().ToList();
        if (items.Count == 0) return;

        var names = string.Join("\n", items.Take(8).Select(i => $"{i.Name} (pid {i.Pid})")) + (items.Count > 8 ? "\n..." : "");
        if (MessageBox.Show(this, $"End {items.Count} process(es) on {_client.Name}?\n\n{names}",
                "Confirm end task", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var failures = new List<string>();
        var ended = new List<ProcessEntry>();
        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            foreach (var p in items)
            {
                _status.Text = $"Ending {p.Name}...";
                try
                {
                    await s.CallAsync(Cmd.ProcKill, new JsonObject
                    {
                        ["pid"] = p.Pid,
                        ["expectedName"] = p.Name,
                        ["expectedStartUtcTicks"] = p.StartUtcTicks,
                    });
                    ended.Add(p);
                }
                catch (RemoteException ex) when (ProcessKillResults.AlreadyGone(ex.Message)) { ended.Add(p); }
                catch (RemoteException ex) { failures.Add($"{p.Name} (pid {p.Pid}): {ex.Message}"); }
            }
        }
        catch (Exception ex) { failures.Add(ex.Message); }

        // Only the ended rows go, so the teacher's sort and scroll position stay; Refresh reloads everything.
        _all.RemoveAll(ended.Contains);
        await Ui.FadeOutAndRemoveAsync(_list, ended);
        _status.Text = $"{ended.Count} process(es) ended on {_client.Name}.";
        if (failures.Count > 0)
            MessageBox.Show(this, string.Join("\n\n", failures), "Some processes could not be ended", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
