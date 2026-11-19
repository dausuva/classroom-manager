// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.ComponentModel;
using System.Windows.Data;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class FileEntry
{
    public string Name { get; init; } = "";
    public bool IsDir { get; init; }
    public long Size { get; init; }
    public string Modified { get; init; } = "";
    public string DisplayName => IsDir ? "[" + Name + "]" : Name;
    public string SizeText => IsDir ? "" : Size < 1024 ? $"{Size} B" : Size < 1048576 ? $"{Size / 1024.0:F1} KB" : $"{Size / 1048576.0:F1} MB";
}

public partial class FileManagerWindow : Window
{
    ClientInfo _client;
    readonly HostSettings _settings;
    string _currentPath = "";
    int _loadVersion;
    string _sortProperty = "";
    ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public FileManagerWindow(ClientInfo client, HostSettings settings)
    {
        InitializeComponent();
        _client = client;
        _settings = settings;
        Title = $"Files - {client.Name} ({client.Address})";
        if (Content is DockPanel root)
        {
            var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var item in ComputerPicker.ForRow(new ComputerPicker(client, SwitchTo))) top.Children.Add(item);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Insert(0, top);
        }
        var copyStatus = new ContextMenu();
        var copyStatusItem = new MenuItem { Header = "Copy status" };
        copyStatusItem.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(StatusText.Text)) Clipboard.SetText(StatusText.Text); };
        copyStatus.Items.Add(copyStatusItem);
        StatusText.ContextMenu = copyStatus;
        FileList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(FileListHeader_Click));
        Loaded += async (_, _) => await StartAsync();
        Ui.RefreshOnF5(this, () => NavigateAsync(_currentPath));
    }

    /// <summary>The computer was changed in the picker: start again at the first folder of its files.</summary>
    async void SwitchTo(ClientInfo computer)
    {
        _client = computer;
        Title = $"Files - {computer.Name} ({computer.Address})";
        _currentPath = "";
        PathBox.Text = "";
        FileList.ItemsSource = new List<FileEntry>();
        StatusText.Text = ComputerPicker.NowShowing(computer);
        await StartAsync();
    }

    async Task StartAsync()
    {
        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            var roots = (await s.CallAsync(Cmd.FsRoots))["roots"]!.AsArray();
            // Start in the first allowed root (normally C:\Users)
            await NavigateAsync(roots.Count > 0 ? roots[0]!.GetValue<string>() : @"C:\Users");
        }
        catch (Exception ex) { Fail(ex); }
    }

    async Task NavigateAsync(string path)
    {
        var preserveSelection = path.Equals(_currentPath, StringComparison.OrdinalIgnoreCase);
        var loadVersion = ++_loadVersion;
        try
        {
            StatusText.Text = "Loading...";
            using var s = await ClientSession.OpenAsync(_client, _settings);
            var data = await s.CallAsync(Cmd.FsList, new JsonObject { ["path"] = path });
            if (loadVersion != _loadVersion) return;

            _currentPath = data["path"]!.GetValue<string>();
            PathBox.Text = _currentPath;

            var entries = data["entries"]!.AsArray()
                .Select(e => new FileEntry
                {
                    Name = e!["name"]!.GetValue<string>(),
                    IsDir = e["isDir"]!.GetValue<bool>(),
                    Size = e["size"]!.GetValue<long>(),
                    Modified = e["modified"]!.GetValue<string>(),
                })
                .ToList();
            entries = SortEntries(entries);
            Ui.ReplaceItemsPreservingSelection(FileList, entries,
                e => $"{(e.IsDir ? "dir" : "file")}\0{e.Name}", preserveSelection);
            StatusText.Text = $"{entries.Count} item(s)";
        }
        catch (Exception ex) { if (loadVersion == _loadVersion) Fail(ex); }
    }

    void FileListHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column?.DisplayMemberBinding is not Binding binding)
            return;
        var property = binding.Path.Path;
        if (_sortProperty.Equals(property, StringComparison.Ordinal))
            _sortDirection = _sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        else
        {
            _sortProperty = property;
            _sortDirection = ListSortDirection.Ascending;
        }
    }

    List<FileEntry> SortEntries(List<FileEntry> entries)
    {
        if (_sortProperty.Length == 0)
            return entries.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Func<FileEntry, object> key = _sortProperty switch
        {
            nameof(FileEntry.DisplayName) => e => e.DisplayName,
            nameof(FileEntry.SizeText) => e => e.Size,
            nameof(FileEntry.Modified) => e => e.Modified,
            _ => e => e.Name,
        };
        return (_sortDirection == ListSortDirection.Ascending
                ? entries.OrderBy(key)
                : entries.OrderByDescending(key))
            .ThenByDescending(e => e.IsDir)
            .ToList();
    }

    string Remote(string name) => _currentPath.TrimEnd('\\') + "\\" + name;

    List<FileEntry> Selected() => FileList.SelectedItems.Cast<FileEntry>().ToList();

    void Fail(Exception ex) => StatusText.Text = "Error: " + ex.Message;

    // ---- navigation ------------------------------------------------------------

    async void Up_Click(object sender, RoutedEventArgs e)
    {
        var parent = System.IO.Path.GetDirectoryName(_currentPath.TrimEnd('\\'));
        if (!string.IsNullOrEmpty(parent)) await NavigateAsync(parent);
    }

    async void Go_Click(object sender, RoutedEventArgs e) => await NavigateAsync(PathText.Clean(PathBox.Text));

    async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await NavigateAsync(PathText.Clean(PathBox.Text));
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await NavigateAsync(_currentPath);

    async void FileList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is FileEntry { IsDir: true } dir) await NavigateAsync(Remote(dir.Name));
    }

    // ---- operations ------------------------------------------------------------

    async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            foreach (var file in dlg.FileNames)
            {
                var name = System.IO.Path.GetFileName(file);
                s.TransferProgress += ShowTransferProgress;
                try { await s.UploadAsync(file, Remote(name)); }
                finally { s.TransferProgress -= ShowTransferProgress; }
            }
            await NavigateAsync(_currentPath);
            StatusText.Text = "Upload complete.";
        }
        catch (Exception ex) { Fail(ex); }
    }

    async void Download_Click(object sender, RoutedEventArgs e)
    {
        var files = Selected().Where(f => !f.IsDir).ToList();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "Select one or more files (folders can't be downloaded).");
            return;
        }

        string targetDir;
        if (files.Count == 1)
        {
            var save = new SaveFileDialog { FileName = files[0].Name };
            if (save.ShowDialog(this) != true) return;
            targetDir = "";
            await DownloadAsync(files, dir: null, singleTarget: save.FileName);
            return;
        }

        var pick = new OpenFolderDialog { Title = "Choose destination folder" };
        if (pick.ShowDialog(this) != true) return;
        targetDir = pick.FolderName;
        await DownloadAsync(files, targetDir, null);
    }

    async Task DownloadAsync(List<FileEntry> files, string? dir, string? singleTarget)
    {
        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            foreach (var f in files)
            {
                var local = singleTarget ?? System.IO.Path.Combine(dir!, f.Name);
                s.TransferProgress += ShowTransferProgress;
                try { await s.DownloadAsync(Remote(f.Name), local); }
                finally { s.TransferProgress -= ShowTransferProgress; }
            }
            StatusText.Text = "Download complete.";
        }
        catch (Exception ex) { Fail(ex); }
    }

    void ShowTransferProgress(FileTransferProgress progress) =>
        StatusText.Text = FileTransferProgressText.Format(progress, _client.Name);

    async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new FormDialog("New folder", this).Text("name", "Folder name");
        if (!dlg.Ask("Create") || dlg.S("name").Length == 0) return;
        await CallAsync(Cmd.FsMkdir, new JsonObject { ["path"] = Remote(dlg.S("name")) });
    }

    async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not [var item]) { MessageBox.Show(this, "Select exactly one item."); return; }
        var dlg = new FormDialog("Rename", this).Text("name", "New name", item.Name);
        if (!dlg.Ask("Rename") || dlg.S("name").Length == 0) return;
        await CallAsync(Cmd.FsRename, new JsonObject
        {
            ["path"] = Remote(item.Name),
            ["newPath"] = Remote(dlg.S("name")),
        });
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        if (items.Count == 0) return;

        var names = string.Join("\n", items.Take(8).Select(i => i.Name)) + (items.Count > 8 ? "\n..." : "");
        if (MessageBox.Show(this, $"Permanently delete {items.Count} item(s) on {_client.Name}?\n\n{names}",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            foreach (var item in items)
                await s.CallAsync(Cmd.FsDelete, new JsonObject { ["path"] = Remote(item.Name) });
            await NavigateAsync(_currentPath);
        }
        catch (Exception ex) { Fail(ex); }
    }

    async Task CallAsync(string cmd, JsonObject args)
    {
        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            await s.CallAsync(cmd, args);
            await NavigateAsync(_currentPath);
        }
        catch (Exception ex) { Fail(ex); }
    }
}
