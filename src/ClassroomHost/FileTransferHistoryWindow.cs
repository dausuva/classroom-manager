// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class FileTransferHistoryWindow : Window
{
    readonly HostSettings _settings;
    readonly IReadOnlyList<ClientInfo> _clients;
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
    readonly Button _undo;
    readonly Button _remove;
    bool _running;

    public FileTransferHistoryWindow(HostSettings settings, IReadOnlyList<ClientInfo> clients, Window owner)
    {
        _settings = settings;
        _clients = clients;
        Owner = owner;
        Title = "File transfer history and undo";
        Width = 1120;
        Height = 620;
        MinWidth = 780;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AddColumn("Created UTC", nameof(FileTransferRecord.CreatedUtc), 175);
        AddColumn("Computer", nameof(FileTransferRecord.Computer), 130);
        AddColumn("File", nameof(FileTransferRecord.FileName), 190);
        AddColumn("Destination", nameof(FileTransferRecord.DestinationPath), 300);
        AddColumn("Bytes", nameof(FileTransferRecord.Size), 90);
        AddColumn("Status", nameof(FileTransferRecord.Status), 190);
        AddColumn("Details", nameof(FileTransferRecord.StatusDetails), 260);

        _undo = Button("_Undo selected deliveries", async (_, _) => await UndoSelectedAsync());
        _remove = Button("_Remove selected records", RemoveSelected);
        var close = Button("_Close", (_, _) => Close());
        close.IsCancel = true;
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_undo);
        buttons.Children.Add(_remove);
        buttons.Children.Add(close);
        var note = Ui.Explanation(
            "Undo deletes only an unchanged delivered file whose current byte length and SHA-256 still match the transfer record. Modified or replaced files are refused.",
            new Thickness(0, 0, 0, 8));
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        root.Children.Add(_grid);
        Content = root;
        Refresh();
    }

    void AddColumn(string header, string path, double width) =>
        _grid.Columns.Add(new DataGridTextColumn { Header = header, Width = width, Binding = new Binding(path) });

    static Button Button(string content, RoutedEventHandler click)
    {
        var button = new Button { Content = content, MinWidth = 145, Margin = new Thickness(0, 0, 8, 8) };
        button.Click += click;
        return button;
    }

    void Refresh()
    {
        _grid.ItemsSource = _settings.FileTransferHistory.OrderByDescending(record => record.CreatedUtc, StringComparer.Ordinal).ToList();
        _status.Text = $"{_settings.FileTransferHistory.Count} transfer record(s); " +
                       $"{_settings.FileTransferHistory.Count(record => record.CanUndo)} currently eligible for verified undo.";
    }

    async Task UndoSelectedAsync()
    {
        if (_running) return;
        var selected = _grid.SelectedItems.Cast<FileTransferRecord>().Where(record => record.CanUndo).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select one or more delivered records that are eligible for undo.");
            return;
        }
        if (MessageBox.Show(this,
                $"Verify and delete {selected.Count} delivered file(s)? Files changed since delivery will be refused.",
                "Confirm transfer undo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        SetRunning(true);
        var failures = new ConcurrentBag<string>();
        try
        {
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(selected.Select(async record =>
            {
                await gate.WaitAsync();
                try
                {
                    var client = FindClient(record);
                    if (client is null) throw new InvalidOperationException("Computer is no longer in the Host list.");
                    if (!client.SupportsCommand(Cmd.FsDeliveredDelete))
                        throw new InvalidOperationException($"Update the Client to protocol {Protocol.RequiredVersion(Cmd.FsDeliveredDelete)} first.");
                    using var session = await ClientSession.OpenAsync(client, _settings);
                    await session.CallAtAsync(Levels.User, Cmd.FsDeliveredDelete, new JsonObject
                    {
                        ["path"] = record.DestinationPath,
                        ["size"] = record.Size,
                        ["sha256"] = record.Sha256,
                    });
                    record.Status = FileTransferStatuses.Undone;
                    record.StatusDetails = "Verified unchanged and deleted.";
                    record.UndoneUtc = DateTime.UtcNow.ToString("O");
                }
                catch (Exception ex)
                {
                    record.Status = FileTransferStatuses.UndoFailed;
                    record.StatusDetails = ex.Message;
                    failures.Add($"{record.Computer} - {record.FileName}: {ex.Message}");
                }
                finally { gate.Release(); }
            }));
            _settings.Save();
        }
        finally { SetRunning(false); }
        Refresh();
        if (!failures.IsEmpty)
            MessageBox.Show(this, string.Join("\n\n", failures), "Some deliveries were not undone", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    ClientInfo? FindClient(FileTransferRecord record) =>
        (record.ClientId.Length == 0 ? null : _clients.FirstOrDefault(client =>
            client.ClientId.Equals(record.ClientId, StringComparison.OrdinalIgnoreCase))) ??
        _clients.FirstOrDefault(client => client.Address.Equals(record.Address, StringComparison.OrdinalIgnoreCase));

    void RemoveSelected(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        var selected = _grid.SelectedItems.Cast<FileTransferRecord>().ToList();
        if (selected.Count == 0) return;
        if (MessageBox.Show(this, $"Remove {selected.Count} history record(s)? This does not delete remote files.",
                "Remove transfer history", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var ids = selected.Select(record => record.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _settings.FileTransferHistory.RemoveAll(record => ids.Contains(record.Id));
        _settings.Save();
        Refresh();
    }

    void SetRunning(bool value)
    {
        _running = value;
        _undo.IsEnabled = !value;
        _remove.IsEnabled = !value;
    }
}
