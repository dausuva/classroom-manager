// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class AutorunEntry
{
    public string Kind { get; init; } = "";        // registry | startup | task
    public string Scope { get; init; } = "";        // machine | user
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Source { get; init; } = "";
    public string MatchedProgram { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string EnabledText => Enabled ? "" : "disabled";

    // Raw fields needed to send the matching removal/block request back - not shown in the grid.
    public JsonObject Raw { get; init; } = new();
}

/// <summary>
/// Shows what launches automatically on one computer - registry Run/RunOnce entries, Startup-folder
/// shortcuts, and (machine level) Scheduled Tasks - each tagged with the installed program it appears
/// to belong to, so you can uninstall the program, block the process, or just remove the autorun hook.
/// </summary>
public sealed class AutorunsWindow : Window
{
    const string MachineLevel = "Machine-wide (elevated)";
    const string UserLevel = "Signed-in user (standard)";

    ClientInfo _client;
    readonly HostSettings _settings;
    readonly ComboBox _level = new() { ItemsSource = new[] { MachineLevel, UserLevel }, SelectedIndex = 1, Width = 200 };
    readonly TextBox _filter = new() { Width = 200, Margin = new Thickness(6, 0, 0, 0) };
    readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    readonly TextBlock _status = Ui.StatusText();
    List<AutorunEntry> _all = new();
    int _loadVersion;

    public AutorunsWindow(ClientInfo client, HostSettings settings, Window owner)
    {
        _client = client;
        _settings = settings;
        Owner = owner;
        Title = $"Autoruns - {client.Name}";
        Width = 1000;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var savedFilter = _settings.FilterState(nameof(AutorunsWindow));
        _filter.Text = savedFilter.Text;
        var savedScope = savedFilter.Selection("Scope", UserLevel);
        if (_level.Items.Contains(savedScope)) _level.SelectedItem = savedScope;

        var grid = new GridView();
        grid.Columns.Add(Col("Kind", "Kind", 70));
        grid.Columns.Add(Col("Name", "Name", 180));
        grid.Columns.Add(Col("Command / target", "Command", 300));
        grid.Columns.Add(Col("Matched program", "MatchedProgram", 180));
        grid.Columns.Add(Col("Status", "EnabledText", 70));
        grid.Columns.Add(Col("Source", "Source", 150));
        _list.View = grid;

        var refresh = new Button { Content = "_Refresh", Margin = new Thickness(6, 0, 0, 0) };
        refresh.Click += async (_, _) => await LoadAsync();
        _level.SelectionChanged += async (_, _) => await LoadAsync();
        _filter.TextChanged += (_, _) => ApplyFilter();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in ComputerPicker.ForRow(new ComputerPicker(client, SwitchTo))) top.Children.Add(item);
        top.Children.Add(Ui.Label("_Scope:", _level, new Thickness(12, 0, 6, 0)));
        top.Children.Add(_level);
        top.Children.Add(Ui.Label("_Filter:", _filter, new Thickness(12, 0, 6, 0)));
        top.Children.Add(_filter);
        top.Children.Add(refresh);

        var uninstall = new Button { Content = "_Uninstall program" };
        uninstall.Click += (_, _) => UninstallProgramHint();
        var block = new Button { Content = "_Block process", Margin = new Thickness(6, 0, 0, 0) };
        block.Click += async (_, _) => await BlockAsync();
        var removeOnly = new Button { Content = "_Remove autorun entry only", Margin = new Thickness(6, 0, 0, 0) };
        removeOnly.Click += async (_, _) => await RemoveAsync();

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(uninstall);
        bottom.Children.Add(block);
        bottom.Children.Add(removeOnly);

        var note = Ui.Explanation(
            "Pick what to do about each entry: uninstall the program it belongs to (if matched), block just the " +
            "process from running, remove only this autorun hook and leave the program installed, or do nothing.",
            new Thickness(0, 0, 0, 6));

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, _) =>
        {
            savedFilter.Text = _filter.Text;
            savedFilter.Selections["Scope"] = _level.SelectedItem as string ?? UserLevel;
            _settings.Save();
        };
        Ui.RefreshOnF5(this, LoadAsync);
        Ui.SelectAllOnCtrlA(this, _list);
    }

    static GridViewColumn Col(string header, string path, double width) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new Binding(path) };

    string Level => _level.SelectedIndex == 1 ? Levels.User : Levels.System;

    /// <summary>The computer was changed in the picker: show it, with an empty list until its data arrives.</summary>
    async void SwitchTo(ClientInfo computer)
    {
        _client = computer;
        Title = $"Autoruns - {computer.Name}";
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
            var data = await s.CallAtAsync(Level, Cmd.AutorunList);
            if (loadVersion != _loadVersion) return;
            _all = data["entries"]!.AsArray().Select(e =>
            {
                var o = e!.AsObject();
                return new AutorunEntry
                {
                    Kind = o["kind"]!.GetValue<string>(),
                    Scope = o["scope"]!.GetValue<string>(),
                    Name = o["name"]!.GetValue<string>(),
                    Command = o["command"]!.GetValue<string>(),
                    Source = o["source"]!.GetValue<string>(),
                    MatchedProgram = o["matchedProgram"]?.GetValue<string>() ?? "",
                    Enabled = o["enabled"]?.GetValue<bool>() ?? true,
                    Raw = o,
                };
            }).OrderBy(e => e.Kind).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ApplyFilter();
        }
        catch (Exception ex) { if (loadVersion == _loadVersion) _status.Text = "Error: " + ex.Message; }
    }

    void ApplyFilter()
    {
        var f = _filter.Text.Trim();
        var shown = _all.Where(e => f.Length == 0 ||
            e.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
            e.Command.Contains(f, StringComparison.OrdinalIgnoreCase) ||
            e.MatchedProgram.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        Ui.ReplaceItemsPreservingSelection(_list, shown,
            e => $"{e.Kind}\0{e.Scope}\0{e.Name}\0{e.Source}");
        _status.Text = $"{shown.Count} autorun entr{(shown.Count == 1 ? "y" : "ies")}";
    }

    List<AutorunEntry> SelectedEntries() => _list.SelectedItems.Cast<AutorunEntry>().ToList();

    void UninstallProgramHint()
    {
        var items = SelectedEntries().Where(e => e.MatchedProgram.Length > 0).ToList();
        if (items.Count == 0) { MessageBox.Show(this, "None of the selected entries are matched to an installed program."); return; }
        // The autorun list doesn't carry the uninstall key/view - point the teacher at Programs..., pre-filtered by name,
        // rather than guessing at silent-uninstall arguments here.
        var names = string.Join(", ", items.Select(i => i.MatchedProgram).Distinct());
        MessageBox.Show(this, $"Open Programs... and use \"Uninstall selected\" for: {names}", "Uninstall from Programs");
    }

    async Task BlockAsync()
    {
        var items = SelectedEntries();
        if (items.Count == 0) return;
        var names = items.Select(e => System.IO.Path.GetFileNameWithoutExtension(FirstToken(e.Command))).Where(n => n.Length > 0).Distinct().ToList();
        if (names.Count == 0) { MessageBox.Show(this, "Could not work out a process name to block for the selected entries."); return; }
        if (MessageBox.Show(this, $"Block these processes from running on {_client.Name}?\n\n{string.Join("\n", names)}",
                "Confirm block", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            await s.CallAsync(Cmd.BlockAppsSet, new JsonObject { ["names"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()) });
            _status.Text = $"Blocking {names.Count} process name(s).";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not apply the block", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    async Task RemoveAsync()
    {
        var items = SelectedEntries();
        if (items.Count == 0) return;
        var names = string.Join("\n", items.Take(8).Select(i => $"[{i.Kind}] {i.Name}")) + (items.Count > 8 ? "\n..." : "");
        if (MessageBox.Show(this, $"Remove {items.Count} autorun entr{(items.Count == 1 ? "y" : "ies")} on {_client.Name}? " +
                "This unhooks the autorun only - the program itself, if any, stays installed.\n\n" + names,
                "Confirm remove", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var failures = new List<string>();
        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            foreach (var e in items)
            {
                try { await s.CallAtAsync(Level, Cmd.AutorunRemove, e.Raw); }
                catch (RemoteException ex) { failures.Add($"{e.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { failures.Add(ex.Message); }
        await LoadAsync();
        if (failures.Count > 0) MessageBox.Show(this, string.Join("\n\n", failures), "Some entries could not be removed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    static string FirstToken(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0) return command[1..end];
        }
        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }
}
