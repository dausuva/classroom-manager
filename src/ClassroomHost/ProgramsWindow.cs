// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class ProgramEntry
{
    public string Key { get; init; } = "";
    public string View { get; init; } = "64";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Publisher { get; init; } = "";
    public bool Silent { get; init; }
    public string InstallLocation { get; init; } = "";
    public string DisplayIcon { get; init; } = "";
    public string SilentSource { get; init; } = "";
    public string SuggestedSilentArgs { get; init; } = "";
    public string SilentNote { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public string SilentText => SilentSource.Length > 0 ? SilentSource : Silent ? "Available" : "Not advertised";

    public static ProgramEntry FromJson(JsonObject item) => new()
    {
        Key = item["key"]!.GetValue<string>(),
        View = item["view"]!.GetValue<string>(),
        Name = item["name"]!.GetValue<string>(),
        Version = item["version"]!.GetValue<string>(),
        Publisher = item["publisher"]!.GetValue<string>(),
        Silent = item["silent"]!.GetValue<bool>(),
        InstallLocation = item["installLocation"]?.GetValue<string>() ?? "",
        DisplayIcon = item["displayIcon"]?.GetValue<string>() ?? "",
        SilentSource = item["silentSource"]?.GetValue<string>() ?? "",
        SuggestedSilentArgs = item["suggestedSilentArgs"]?.GetValue<string>() ?? "",
        SilentNote = item["silentNote"]?.GetValue<string>() ?? "",
        UninstallString = item["uninstallString"]?.GetValue<string>() ?? "",
        QuietUninstallString = item["quietUninstallString"]?.GetValue<string>() ?? "",
    };
}

/// <summary>Lists installed programs on one computer (machine-wide or current user) and uninstalls them.</summary>
public sealed class ProgramsWindow : Window
{
    const string MachineLevel = "Machine-wide (elevated)";
    const string UserLevel = "Signed-in user (standard)";

    ClientInfo _client;
    readonly HostSettings _settings;
    readonly ComboBox _level = new() { ItemsSource = new[] { MachineLevel, UserLevel }, SelectedIndex = 0, Width = 200 };
    readonly TextBox _filter = new() { Width = 200, Margin = new Thickness(6, 0, 0, 0) };
    readonly TextBox _args = new() { Margin = new Thickness(6, 0, 0, 0), MinWidth = 220 };
    readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    readonly TextBlock _status = Ui.StatusText();
    readonly string _preferredExecutablePath;
    readonly string _preferredProcessName;
    readonly ProgramWindowActions? _actions;
    string _sourceLabel = "remote computer";
    bool _selectionApplied;
    List<ProgramEntry> _all = new();
    int _loadVersion;

    public ProgramsWindow(ClientInfo client, HostSettings settings, Window owner,
        string preferredExecutablePath = "", string preferredProcessName = "", ProgramWindowActions? actions = null)
    {
        _client = client;
        _actions = actions;
        _settings = settings;
        _preferredExecutablePath = preferredExecutablePath;
        _preferredProcessName = preferredProcessName;
        Owner = owner;
        Title = $"Programs - {client.Name} ({client.Address})";
        Width = 820;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var savedFilter = _settings.FilterState(nameof(ProgramsWindow));
        _filter.Text = savedFilter.Text;
        var savedScope = savedFilter.Selection("Scope", MachineLevel);
        if (_level.Items.Contains(savedScope)) _level.SelectedItem = savedScope;

        var grid = new GridView();
        grid.Columns.Add(Col("Name", "Name", 340));
        grid.Columns.Add(Col("Version", "Version", 110));
        grid.Columns.Add(Col("Publisher", "Publisher", 170));
        grid.Columns.Add(Col("Silent uninstall", "SilentText", 200));
        _list.View = grid;

        var refresh = new Button { Content = "_Refresh", ToolTip = "Refresh (F5) — reload installed programs from the selected remote computer.", Margin = new Thickness(6, 0, 0, 0) };
        refresh.Click += async (_, _) => await LoadAsync();
        var uninstall = new Button { Content = "_Uninstall selected", ToolTip = "Uninstall selected — run each selected program's remote uninstall command.", Margin = new Thickness(6, 0, 0, 0) };
        uninstall.Click += async (_, _) => await UninstallAsync();
        var details = new Button
        {
            Content = "_Details...",
            ToolTip = "Inspect and copy uninstall commands or review a detected silent-switch suggestion.",
            Margin = new Thickness(6, 0, 0, 0),
        };
        details.Click += (_, _) => ShowDetails();
        _level.SelectionChanged += async (_, _) => { _selectionApplied = false; await LoadAsync(); };
        _filter.TextChanged += (_, _) => ApplyFilter();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in ComputerPicker.ForRow(new ComputerPicker(client, SwitchTo))) top.Children.Add(item);
        top.Children.Add(Ui.Label("_Scope:", _level, new Thickness(12, 0, 6, 0)));
        top.Children.Add(_level);
        top.Children.Add(Ui.Label("_Filter:", _filter, new Thickness(12, 0, 6, 0)));
        top.Children.Add(_filter);
        top.Children.Add(refresh);
        top.Children.Add(details);
        if (actions is not null)
        {
            var open = new Button { Content = "O_pen selected...", Tag = "\u25B6", ToolTip = "Open selected — start the selected program on this computer for the signed-in user.", Margin = new Thickness(6, 0, 0, 0) };
            open.Click += async (_, _) => await OpenSelectedAsync();
            var install = new Button { Content = "_Install another program...", Tag = "\uD83D\uDCE6", ToolTip = "Install another program — open the install dialog for this computer.", Margin = new Thickness(6, 0, 0, 0) };
            install.Click += (_, _) => actions.Install([_client]);
            top.Children.Add(open);
            top.Children.Add(install);
        }

        var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(uninstall, Dock.Right);
        bottom.Children.Add(uninstall);
        var argsLabel = Ui.Label("Silent s_witches (only if needed):", _args);
        DockPanel.SetDock(argsLabel, Dock.Left);
        bottom.Children.Add(argsLabel);
        bottom.Children.Add(_args);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_status);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, _) =>
        {
            savedFilter.Text = _filter.Text;
            savedFilter.Selections["Scope"] = _level.SelectedItem as string ?? MachineLevel;
            _settings.Save();
        };
        Ui.RefreshOnF5(this, LoadAsync);
    }

    static GridViewColumn Col(string header, string path, double width) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new Binding(path) };

    string Level => _level.SelectedIndex == 1 ? Levels.User : Levels.System;

    /// <summary>The computer was changed in the picker: show it, with an empty list until its data arrives.</summary>
    async void SwitchTo(ClientInfo computer)
    {
        _client = computer;
        Title = $"Programs - {computer.Name} ({computer.Address})";
        _all = new();
        _sourceLabel = "remote computer";
        _list.ItemsSource = new List<ProgramEntry>();
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
            var (software, machine) = ModuleRuns.OpenSoftware(s, _client);
            var data = JsonNode.Parse(await software.ListAsync(machine, Level == Levels.System, CancellationToken.None))!.AsObject();
            if (loadVersion != _loadVersion) return;
            var machineName = data["machineName"]?.GetValue<string>() ?? _client.Name;
            var scope = data["scope"]?.GetValue<string>() ?? (_level.SelectedIndex == 1 ? "user" : "machine");
            _sourceLabel = $"{machineName} ({_client.Address}, {scope} scope)";
            _all = data["programs"]!.AsArray().Select(p => ProgramEntry.FromJson(p!.AsObject())).ToList();
            ApplyFilter();
            ApplyPreferredProcessSelection();
        }
        catch (Exception ex) { if (loadVersion == _loadVersion) _status.Text = "Error: " + ex.Message; }
    }

    void ApplyFilter()
    {
        var f = _filter.Text.Trim();
        // The program's name is what a person remembers, but a process name ("msedge") is only in the file
        // name, and a vendor in the publisher or folder: every word must be found somewhere in these.
        var shown = _all.Where(p => FilterMatch.Matches(f, p.Name, p.Publisher, p.InstallLocation, p.DisplayIcon, p.UninstallString, p.QuietUninstallString)).ToList();
        Ui.ReplaceItemsPreservingSelection(_list, shown, p => $"{p.View}\0{p.Key}");
        var byName = f.Length == 0 ? shown.Count : shown.Count(p => FilterMatch.Matches(f, p.Name));
        _status.Text = f.Length > 0 && shown.Count == 0
            ? $"No program matches \"{f}\" by name, publisher, folder or file name. Programs installed only for the signed-in user are in the other Scope."
            : byName < shown.Count
                ? $"{shown.Count} program(s) loaded from {_sourceLabel}; {shown.Count - byName} found by publisher, folder or file name"
                : $"{shown.Count} program(s) loaded from {_sourceLabel}";
    }

    void ApplyPreferredProcessSelection()
    {
        if (_selectionApplied || (_preferredExecutablePath.Length == 0 && _preferredProcessName.Length == 0)) return;
        _selectionApplied = true;

        ProgramEntry? match = null;
        if (_preferredExecutablePath.Length > 0)
        {
            match = _all.FirstOrDefault(p => ProgramLaunch.DisplayIconPath(p.DisplayIcon)
                .Equals(_preferredExecutablePath, StringComparison.OrdinalIgnoreCase));
            match ??= _all.Where(p => p.InstallLocation.Length > 3 &&
                    _preferredExecutablePath.StartsWith(p.InstallLocation.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.InstallLocation.Length).FirstOrDefault();
        }
        var processName = System.IO.Path.GetFileNameWithoutExtension(_preferredProcessName);
        match ??= _all.FirstOrDefault(p => processName.Length > 1 &&
            p.Name.Contains(processName, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            _filter.Text = match.Name;
            _list.SelectedItem = match;
            _list.ScrollIntoView(match);
            _status.Text = $"Matched '{_preferredProcessName}' to '{match.Name}' on {_sourceLabel}. Review before uninstalling.";
        }
        else
        {
            _filter.Text = processName;
            _status.Text = $"No uninstall entry matched '{_preferredProcessName}' on {_sourceLabel}. Try another filter.";
        }
    }

    async Task OpenSelectedAsync()
    {
        if (_actions is null) return;
        if (_list.SelectedItems.Count != 1)
        {
            MessageBox.Show(this, "Select one program to open.", "Open selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await _actions.OpenProgram([_client], ProgramLaunch.Suggestion([(ProgramEntry)_list.SelectedItem]));
    }

    void ShowDetails()
    {
        if (_list.SelectedItems.Count != 1)
        {
            MessageBox.Show(this, "Select one program to inspect its uninstall details.", "Uninstall details", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var entry = (ProgramEntry)_list.SelectedItem;
        var window = new UninstallDetailsWindow(this, entry);
        if (window.ShowDialog() == true && entry.SuggestedSilentArgs.Length > 0)
        {
            _args.Text = entry.SuggestedSilentArgs;
            _status.Text = $"Suggested switches loaded for {entry.Name}. Review them and test on one computer before wider use.";
        }
    }

    async Task UninstallAsync()
    {
        var items = _list.SelectedItems.Cast<ProgramEntry>().ToList();
        if (items.Count == 0) return;

        var names = string.Join("\n", items.Take(8).Select(i => i.Name)) + (items.Count > 8 ? "\n..." : "");
        if (MessageBox.Show(this, $"Uninstall {items.Count} program(s) from {_client.Name}?\n\n{names}",
                "Confirm uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var failures = new List<string>();
        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            var (software, machine) = ModuleRuns.OpenSoftware(s, _client);
            foreach (var p in items)
            {
                _status.Text = $"Uninstalling {p.Name}...";
                try
                {
                    await software.UninstallAsync(machine, Level == Levels.System, p.Key, p.View, _args.Text.Trim(), CancellationToken.None);
                }
                catch (RemoteException ex) { failures.Add($"{p.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { failures.Add(ex.Message); }

        await LoadAsync();
        if (failures.Count > 0)
            MessageBox.Show(this, string.Join("\n\n", failures), "Some uninstalls failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
