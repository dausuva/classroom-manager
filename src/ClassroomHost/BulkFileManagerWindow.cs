// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ClassroomManager.Shared;
using Microsoft.Win32;

namespace ClassroomManager.Host;

public sealed record BulkFilePresence(ClientInfo Client, string Name, bool IsDir, long Size, string Modified);

public sealed class BulkFileRow
{
    public string Name { get; init; } = "";
    public bool IsDir { get; init; }
    public string DisplayName => IsDir ? "[" + Name + "]" : Name;
    public string Type => IsDir ? "Folder" : "File";
    public string Present { get; init; } = "";
    public string Sizes { get; init; } = "";
    public string Modified { get; init; } = "";
    public string Computers { get; init; } = "";
    public IReadOnlyList<BulkFilePresence> Entries { get; init; } = [];
}

public static class BulkFileComparison
{
    public static List<BulkFileRow> Build(IReadOnlyList<ClientInfo> clients, IEnumerable<BulkFilePresence> entries) =>
        entries.GroupBy(entry => $"{(entry.IsDir ? "D" : "F")}\0{entry.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var present = group.OrderBy(entry => entry.Client.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var sizes = present.Where(entry => !entry.IsDir).Select(entry => entry.Size).Distinct().Order().ToList();
                var modified = present.Select(entry => entry.Modified).Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
                return new BulkFileRow
                {
                    Name = present[0].Name,
                    IsDir = present[0].IsDir,
                    Present = $"{present.Select(entry => entry.Client).Distinct().Count()} / {clients.Count}",
                    Sizes = sizes.Count == 0 ? "" : string.Join(" / ", sizes.Select(StagedUploadsWindow.FormatSize)),
                    Modified = modified.Count == 0 ? "" : string.Join(" / ", modified.Take(3)) + (modified.Count > 3 ? " / ..." : ""),
                    Computers = string.Join(", ", present.Select(entry => entry.Client.Name).Distinct(StringComparer.OrdinalIgnoreCase)),
                    Entries = present,
                };
            })
            .OrderByDescending(row => row.IsDir)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public sealed class BulkFileManagerWindow : Window
{
    readonly IReadOnlyList<ClientInfo> _clients;
    readonly HostSettings _settings;
    readonly TextBox _path = new() { Text = @"C:\Users", Margin = new Thickness(0, 0, 6, 0) };
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
    readonly List<Button> _buttons = [];
    bool _running;
    string _currentPath = @"C:\Users";
    int _loadVersion;

    public BulkFileManagerWindow(IReadOnlyList<ClientInfo> clients, HostSettings settings, Window owner)
    {
        _clients = clients;
        _settings = settings;
        Owner = owner;
        Title = $"Files comparison - {clients.Count} computers";
        Width = 1180;
        Height = 680;
        MinWidth = 820;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AddColumn("Name", nameof(BulkFileRow.DisplayName), 280);
        AddColumn("Type", nameof(BulkFileRow.Type), 75);
        AddColumn("Present", nameof(BulkFileRow.Present), 85);
        AddColumn("Size(s)", nameof(BulkFileRow.Sizes), 140);
        AddColumn("Modified", nameof(BulkFileRow.Modified), 220);
        AddColumn("Computers", nameof(BulkFileRow.Computers), 330);

        var up = Button("_Up", async (_, _) => await UpAsync());
        var go = Button("_Go", async (_, _) => await NavigateAsync(PathText.Clean(_path.Text)));
        var pathRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(up, Dock.Left);
        DockPanel.SetDock(go, Dock.Right);
        var label = Ui.Label("Shared _path:", _path, new Thickness(8, 0, 6, 0));
        DockPanel.SetDock(label, Dock.Left);
        pathRow.Children.Add(up);
        pathRow.Children.Add(go);
        pathRow.Children.Add(label);
        pathRow.Children.Add(_path);

        var refresh = Button("_Refresh", async (_, _) => await NavigateAsync(_currentPath));
        var upload = Button("_Upload files...", async (_, _) => await UploadAsync());
        var folder = Button("_New folder...", async (_, _) => await NewFolderAsync());
        var delete = Button("_Delete selected", async (_, _) => await DeleteSelectedAsync());
        var close = Button("_Close", (_, _) => Close());
        close.IsCancel = true;
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var button in new[] { refresh, upload, folder, delete, close }) actions.Children.Add(button);

        var note = Ui.Explanation(
            "This is a shared-path comparison: each row shows which selected computers contain that name. Open a folder to query the same path everywhere. Bulk upload and folder creation target every selected computer; delete affects only computers where the selected row exists.",
            new Thickness(0, 0, 0, 8));
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(pathRow, Dock.Top);
        DockPanel.SetDock(actions, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(pathRow);
        root.Children.Add(actions);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(_grid);
        Content = root;

        _path.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await NavigateAsync(PathText.Clean(_path.Text)); };
        _grid.MouseDoubleClick += async (_, _) =>
        {
            if (_grid.SelectedItem is BulkFileRow { IsDir: true } row)
                await NavigateAsync(Remote(row.Name));
        };
        Loaded += async (_, _) => await NavigateAsync(_currentPath);
        Ui.RefreshOnF5(this, () => NavigateAsync(_currentPath));
    }

    void AddColumn(string header, string path, double width) =>
        _grid.Columns.Add(new DataGridTextColumn { Header = header, Width = width, Binding = new Binding(path) });

    Button Button(string content, RoutedEventHandler click)
    {
        var button = new Button { Content = content, MinWidth = 105, Margin = new Thickness(0, 0, 8, 8) };
        button.Click += click;
        _buttons.Add(button);
        return button;
    }

    string Remote(string name) => _currentPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + name;

    async Task NavigateAsync(string path)
    {
        if (_running || path.Length == 0) return;
        var version = ++_loadVersion;
        SetRunning(true);
        var errors = new ConcurrentBag<string>();
        try
        {
            _status.Text = $"Loading {path} from {_clients.Count} computers...";
            using var gate = new SemaphoreSlim(8);
            var results = await Task.WhenAll(_clients.Select(async client =>
            {
                await gate.WaitAsync();
                try
                {
                    using var session = await ClientSession.OpenAsync(client, _settings);
                    var data = await session.CallAsync(Cmd.FsList, new JsonObject { ["path"] = path });
                    return data["entries"]!.AsArray().Select(node =>
                    {
                        var item = node!.AsObject();
                        return new BulkFilePresence(client,
                            item["name"]?.GetValue<string>() ?? "",
                            item["isDir"]?.GetValue<bool>() == true,
                            item["size"]?.GetValue<long>() ?? 0,
                            item["modified"]?.GetValue<string>() ?? "");
                    }).Where(entry => entry.Name.Length > 0).ToList();
                }
                catch (Exception ex)
                {
                    errors.Add($"{client.Name}: {ex.Message}");
                    return [];
                }
                finally { gate.Release(); }
            }));
            if (version != _loadVersion) return;
            _currentPath = path;
            _path.Text = path;
            var rows = BulkFileComparison.Build(_clients, results.SelectMany(result => result));
            _grid.ItemsSource = rows;
            _status.Text = $"{rows.Count} distinct item(s); {_clients.Count - errors.Count} of {_clients.Count} computers available." +
                           (errors.IsEmpty ? "" : " Unavailable: " + string.Join("; ", errors));
        }
        finally { SetRunning(false); }
    }

    async Task UpAsync()
    {
        var parent = Path.GetDirectoryName(_currentPath.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(parent)) await NavigateAsync(parent);
    }

    async Task UploadAsync()
    {
        if (_running) return;
        var picker = new OpenFileDialog { Multiselect = true, Title = "Upload files to every selected computer" };
        if (picker.ShowDialog(this) != true) return;
        var total = picker.FileNames.Sum(file => new FileInfo(file).Length);
        if (MessageBox.Show(this,
                $"Upload {picker.FileNames.Length} file(s), {StagedUploadsWindow.FormatSize(total)}, to {_currentPath} on {_clients.Count} computers? Existing files with the same names will be replaced.",
                "Confirm bulk upload", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await RunAcrossClientsAsync("Uploading files", async (client, session) =>
        {
            foreach (var file in picker.FileNames)
                await session.UploadAsync(file, Path.Combine(_currentPath, Path.GetFileName(file)));
        });
        await NavigateAsync(_currentPath);
    }

    async Task NewFolderAsync()
    {
        if (_running) return;
        var dialog = new FormDialog("Create folder on selected computers", this).Text("name", "Folder name");
        if (!dialog.Ask("Create") || dialog.S("name").Length == 0) return;
        var name = dialog.S("name");
        if (Path.GetFileName(name) != name || name is "." or "..")
        {
            MessageBox.Show(this, "Enter one folder name without path separators.");
            return;
        }
        await RunAcrossClientsAsync("Creating folder", (_, session) =>
            session.CallAsync(Cmd.FsMkdir, new JsonObject { ["path"] = Path.Combine(_currentPath, name) }));
        await NavigateAsync(_currentPath);
    }

    async Task DeleteSelectedAsync()
    {
        if (_running) return;
        var rows = _grid.SelectedItems.Cast<BulkFileRow>().ToList();
        if (rows.Count == 0) return;
        var entries = rows.SelectMany(row => row.Entries).ToList();
        if (MessageBox.Show(this,
                $"Permanently delete {rows.Count} selected name(s) from {entries.Count} computer location(s)?\n\n" +
                string.Join("\n", rows.Take(12).Select(row => row.DisplayName)) + (rows.Count > 12 ? "\n..." : ""),
                "Confirm bulk delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAcrossClientsAsync("Deleting selected items", async (client, session) =>
        {
            foreach (var entry in entries.Where(entry => ReferenceEquals(entry.Client, client)))
                await session.CallAsync(Cmd.FsDelete, new JsonObject { ["path"] = Path.Combine(_currentPath, entry.Name) });
        });
        await NavigateAsync(_currentPath);
    }

    async Task RunAcrossClientsAsync(string action, Func<ClientInfo, ClientSession, Task> operation)
    {
        SetRunning(true);
        var failures = new ConcurrentBag<string>();
        try
        {
            _status.Text = action + "...";
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(_clients.Select(async client =>
            {
                await gate.WaitAsync();
                try
                {
                    using var session = await ClientSession.OpenAsync(client, _settings);
                    await operation(client, session);
                }
                catch (Exception ex) { failures.Add($"{client.Name}: {ex.Message}"); }
                finally { gate.Release(); }
            }));
        }
        finally { SetRunning(false); }
        if (!failures.IsEmpty)
            MessageBox.Show(this, string.Join("\n\n", failures), $"{action} - partial failure", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void SetRunning(bool value)
    {
        _running = value;
        _path.IsEnabled = !value;
        foreach (var button in _buttons) button.IsEnabled = !value;
    }
}
