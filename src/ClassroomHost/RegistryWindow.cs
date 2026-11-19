// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class RegistryRow
{
    public bool IsKey { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";  // "Key" or a RegistryValueKind name
    public string Data { get; init; } = "";
}

/// <summary>
/// Ad-hoc registry browser/editor for one computer: navigate a hive/path, add or edit a value,
/// delete a value or a whole key, and export the current key to a .reg file or import one
/// (the same import path Install... uses to "push a .reg as a program").
/// </summary>
public sealed class RegistryWindow : Window
{
    ClientInfo _client;
    readonly HostSettings _settings;
    readonly ComboBox _level = new() { ItemsSource = new[] { "Machine-wide (elevated)", "Signed-in user (standard)" }, SelectedIndex = 0, Width = 190 };
    readonly ComboBox _hive = new() { ItemsSource = new[] { "HKLM", "HKCU", "HKCR", "HKU", "HKCC" }, SelectedIndex = 0, Width = 80, Margin = new Thickness(6, 0, 0, 0) };
    readonly ComboBox _view = new() { ItemsSource = new[] { "64-bit", "32-bit" }, SelectedIndex = 0, Width = 80, Margin = new Thickness(6, 0, 0, 0) };
    readonly TextBox _path = new() { Margin = new Thickness(6, 0, 6, 0), MinWidth = 320 };
    readonly ListView _list = new() { SelectionMode = SelectionMode.Single };
    readonly TextBlock _status = Ui.StatusText();
    string _loadedLocation = "";
    int _loadVersion;

    public RegistryWindow(ClientInfo client, HostSettings settings, Window owner)
    {
        _client = client;
        _settings = settings;
        Owner = owner;
        Title = $"Registry - {client.Name}";
        Width = 900;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Name", Width = 260, DisplayMemberBinding = new Binding("Name") });
        grid.Columns.Add(new GridViewColumn { Header = "Type", Width = 110, DisplayMemberBinding = new Binding("Type") });
        grid.Columns.Add(new GridViewColumn { Header = "Data", Width = 420, DisplayMemberBinding = new Binding("Data") });
        _list.View = grid;
        _list.MouseDoubleClick += (_, _) => Descend();

        var go = new Button { Content = "_Go", Margin = new Thickness(0, 0, 6, 0) };
        go.Click += async (_, _) => await LoadAsync();
        var up = new Button { Content = "_Up" };
        up.Click += async (_, _) => { GoUp(); await LoadAsync(); };
        _path.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) await LoadAsync(); };
        _level.SelectionChanged += async (_, _) => await LoadAsync();
        _hive.SelectionChanged += async (_, _) => { _path.Text = ""; await LoadAsync(); };
        _view.SelectionChanged += async (_, _) => await LoadAsync();

        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in ComputerPicker.ForRow(new ComputerPicker(client, SwitchTo))) top.Children.Add(item);
        top.Children.Add(Ui.Label("_Scope:", _level, new Thickness(12, 0, 6, 0)));
        top.Children.Add(_level);
        top.Children.Add(Ui.Label("_Hive:", _hive, new Thickness(8, 0, 6, 0)));
        top.Children.Add(_hive);
        top.Children.Add(Ui.Label("_View:", _view, new Thickness(8, 0, 6, 0)));
        top.Children.Add(_view);
        top.Children.Add(Ui.Label("_Path:", _path, new Thickness(8, 0, 6, 0)));
        top.Children.Add(_path);
        top.Children.Add(go);
        top.Children.Add(up);
        Ui.Spaced(top);

        var addValue = new Button { Content = "_Add/edit value..." };
        addValue.Click += async (_, _) => await AddOrEditValueAsync();
        var deleteRow = new Button { Content = "_Delete selected...", Margin = new Thickness(6, 0, 0, 0) };
        deleteRow.Click += async (_, _) => await DeleteAsync();
        var exportBtn = new Button { Content = "_Export key...", Margin = new Thickness(6, 0, 0, 0) };
        exportBtn.Click += async (_, _) => await ExportAsync();
        var importBtn = new Button { Content = "_Import .reg file...", Margin = new Thickness(6, 0, 0, 0) };
        importBtn.Click += async (_, _) => await ImportAsync();

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(addValue);
        bottom.Children.Add(deleteRow);
        bottom.Children.Add(exportBtn);
        bottom.Children.Add(importBtn);

        var note = Ui.Explanation(
            "Double-click a key to open it. Editing the registry directly can stop programs (or Windows) from " +
            "starting - there's no undo beyond exporting first. SYSTEM/SECURITY/SAM are refused outright.",
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
        Ui.RefreshOnF5(this, LoadAsync);
    }

    string Level => _level.SelectedIndex == 1 ? Levels.User : Levels.System;
    string Hive => (string)_hive.SelectedItem;
    string View => _view.SelectedIndex == 1 ? "32" : "64";
    string Path => _path.Text.Trim('\\');

    void GoUp()
    {
        var p = Path;
        if (p.Length == 0) return;
        _path.Text = p.Contains('\\') ? p[..p.LastIndexOf('\\')] : "";
    }

    void Descend()
    {
        if (_list.SelectedItem is not RegistryRow row || !row.IsKey) return;
        _path.Text = Path.Length == 0 ? row.Name : $@"{Path}\{row.Name}";
        _ = LoadAsync();
    }

    JsonObject Args(JsonObject? extra = null)
    {
        var a = new JsonObject { ["hive"] = Hive, ["view"] = View, ["path"] = Path };
        if (extra is not null) foreach (var (k, v) in extra) a[k] = v?.DeepClone();
        return a;
    }

    /// <summary>The computer was changed in the picker: start again at the top of its registry.</summary>
    async void SwitchTo(ClientInfo computer)
    {
        _client = computer;
        Title = $"Registry - {computer.Name}";
        _path.Text = "";
        _loadedLocation = "";
        _list.ItemsSource = new List<RegistryRow>();
        _status.Text = ComputerPicker.NowShowing(computer);
        await LoadAsync();
    }

    async Task LoadAsync()
    {
        var location = $"{Level}\0{Hive}\0{View}\0{Path}";
        var preserveSelection = location.Equals(_loadedLocation, StringComparison.OrdinalIgnoreCase);
        var loadVersion = ++_loadVersion;
        try
        {
            _status.Text = "Loading...";
            using var s = await ClientSession.OpenAsync(_client, _settings);
            var data = await s.CallAtAsync(Level, Cmd.RegistryList, Args());
            if (loadVersion != _loadVersion) return;
            var rows = new List<RegistryRow>();
            foreach (var k in data["subkeys"]!.AsArray())
                rows.Add(new RegistryRow { IsKey = true, Name = k!.GetValue<string>(), Type = "Key" });
            foreach (var v in data["values"]!.AsArray())
                rows.Add(new RegistryRow { IsKey = false, Name = v!["name"]!.GetValue<string>(), Type = v["type"]!.GetValue<string>(), Data = v["data"]!.GetValue<string>() });
            Ui.ReplaceItemsPreservingSelection(_list, rows,
                r => $"{(r.IsKey ? "key" : "value")}\0{r.Name}", preserveSelection);
            _loadedLocation = location;
            _status.Text = $"{rows.Count(r => r.IsKey)} subkey(s), {rows.Count(r => !r.IsKey)} value(s)";
        }
        catch (Exception ex) { if (loadVersion == _loadVersion) _status.Text = "Error: " + ex.Message; }
    }

    async Task AddOrEditValueAsync()
    {
        var existing = _list.SelectedItem as RegistryRow;
        if (existing?.IsKey == true) { MessageBox.Show(this, "Select a value, not a key (or nothing, to add a new value)."); return; }

        var types = new[] { "String", "ExpandString", "DWord", "QWord", "MultiString", "Binary" };
        var typeIndex = existing is null ? 0 : Math.Max(0, Array.IndexOf(types, existing.Type));

        var dlg = new FormDialog(existing is null ? "Add value" : $"Edit value - {existing.Name}", this)
            .Text("name", "Value name (blank = (Default))", existing?.Name == "(Default)" ? "" : existing?.Name ?? "")
            .Combo("type", "Type", types, typeIndex)
            .Text("data", "Data" + (existing?.Type == "Binary" ? " (hex, e.g. 01 0A FF)" : existing?.Type == "MultiString" ? " (one per line)" : ""), existing?.Data ?? "");
        if (!dlg.Ask("Save")) return;

        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            await s.CallAtAsync(Level, Cmd.RegistrySetValue, Args(new JsonObject
            {
                ["name"] = dlg.S("name").Length == 0 ? "(Default)" : dlg.S("name"),
                ["type"] = dlg.S("type"),
                ["data"] = dlg.S("data"),
            }));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save the value", MessageBoxButton.OK, MessageBoxImage.Warning); }
        await LoadAsync();
    }

    async Task DeleteAsync()
    {
        if (_list.SelectedItem is not RegistryRow row) { MessageBox.Show(this, "Select a value or key first."); return; }
        var what = row.IsKey ? $"the key \"{row.Name}\" and everything under it" : $"the value \"{row.Name}\"";
        if (MessageBox.Show(this, $"Delete {what} on {_client.Name}? This cannot be undone.",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            if (row.IsKey)
                await s.CallAtAsync(Level, Cmd.RegistryDeleteKey, Args(new JsonObject { ["path"] = Path.Length == 0 ? row.Name : $@"{Path}\{row.Name}" }));
            else
                await s.CallAtAsync(Level, Cmd.RegistryDeleteValue, Args(new JsonObject { ["name"] = row.Name == "(Default)" ? "" : row.Name }));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not delete", MessageBoxButton.OK, MessageBoxImage.Warning); }
        await LoadAsync();
    }

    async Task ExportAsync()
    {
        if (Path.Length == 0) { MessageBox.Show(this, "Navigate into a key first - exporting a whole hive isn't supported here."); return; }

        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"{Hive}_{Path.Replace('\\', '_')}.reg", Filter = "Registry files (*.reg)|*.reg" };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            _status.Text = "Exporting...";
            using var s = await ClientSession.OpenAsync(_client, _settings);
            var fileName = System.IO.Path.GetFileName(dlg.FileName);
            var data = await s.CallAtAsync(Level, Cmd.RegistryExport, Args(new JsonObject { ["fileName"] = fileName }));
            var remote = data["path"]!.GetValue<string>();
            await s.DownloadAsync(remote, dlg.FileName);
            try { await s.CallAsync(Cmd.FsDelete, new JsonObject { ["path"] = remote }); } catch { }
            _status.Text = $"Exported to {dlg.FileName}";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    async Task ImportAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Registry files (*.reg)|*.reg" };
        if (dlg.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, $"Apply \"{System.IO.Path.GetFileName(dlg.FileName)}\" to {_client.Name}'s registry now? " +
                "This edits the registry directly with no undo - review the file first if you haven't already.",
                "Confirm import", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            _status.Text = "Uploading...";
            using var s = await ClientSession.OpenAsync(_client, _settings);
            var remote = $@"{MyStaging}\{System.IO.Path.GetFileName(dlg.FileName)}";
            await s.UploadAsync(dlg.FileName, remote);
            _status.Text = "Applying...";
            await s.CallAtAsync(Level, Cmd.RegistryImport, new JsonObject { ["path"] = remote });
            try { await s.CallAsync(Cmd.FsDelete, new JsonObject { ["path"] = remote }); } catch { }
            _status.Text = "Import applied.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
        await LoadAsync();
    }

    string MyStaging => $@"{StagingRoot}\{_settings.HostId}";
    const string StagingRoot = @"C:\ProgramData\ClassroomManager\Staging";
}
