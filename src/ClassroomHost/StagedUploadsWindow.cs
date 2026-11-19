// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;
using Microsoft.Win32;

namespace ClassroomManager.Host;

public sealed record StagedUploadPresence(ClientInfo Client, string RelativePath, long Size, DateTimeOffset ModifiedUtc);

public sealed record StagedUploadOperation(string Key, long Bytes);

public sealed class StagedUploadProgressTracker
{
    readonly object _gate = new();
    readonly Dictionary<string, (long Bytes, double Fraction)> _operations;
    readonly long _totalBytes;

    public StagedUploadProgressTracker(IEnumerable<StagedUploadOperation> operations)
    {
        _operations = operations.ToDictionary(operation => operation.Key,
            operation => (Math.Max(1, operation.Bytes), 0d), StringComparer.Ordinal);
        _totalBytes = _operations.Values.Sum(operation => operation.Bytes);
    }

    public double Report(string key, double fraction)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(key, out var operation)) return FractionUnsafe();
            var bounded = Math.Clamp(fraction, 0, 1);
            _operations[key] = (operation.Bytes, Math.Max(operation.Fraction, bounded));
            return FractionUnsafe();
        }
    }

    public double Fraction
    {
        get { lock (_gate) return FractionUnsafe(); }
    }

    double FractionUnsafe() => _totalBytes == 0 ? 1 :
        _operations.Values.Sum(operation => operation.Bytes * operation.Fraction) / _totalBytes;
}

public sealed class StagedUploadRow
{
    public string RelativePath { get; init; } = "";
    public string Purpose { get; init; } = "";
    public string Size { get; init; } = "";
    public string Available { get; init; } = "";
    public string Computers { get; init; } = "";
    public string Modified { get; init; } = "";
    public IReadOnlyList<StagedUploadPresence> Entries { get; init; } = [];

    public bool CanInstallOnAll(int targetCount)
    {
        var extension = Path.GetExtension(RelativePath);
        return (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".msi", StringComparison.OrdinalIgnoreCase)) &&
               Entries.Select(value => value.Client).Distinct().Count() == targetCount &&
               Entries.Select(value => value.Size).Distinct().Count() == 1;
    }
}

public sealed class StagedUploadsWindow : Window
{
    readonly IReadOnlyList<ClientInfo> _clients;
    readonly HostSettings _settings;
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
    readonly TextBlock _queueStatus = Ui.MutedText("", new Thickness(0, 4, 0, 0));
    readonly TextBlock _emptyState = Ui.MutedText(
        "No temporary uploads found. Choose Add installers to upload once to all selected computers.");
    readonly ProgressBar _progress = new()
    {
        Minimum = 0,
        Maximum = 1,
        Height = 8,
        Margin = new Thickness(0, 6, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    readonly List<Button> _actionButtons = [];
    readonly Queue<StagedUploadBatch> _uploadQueue = new();
    readonly Button _uploadButton;
    bool _running;
    bool _uploading;

    public string SelectedInstallRelativePath { get; private set; } = "";

    public StagedUploadsWindow(IReadOnlyList<ClientInfo> clients, HostSettings settings, Window owner)
    {
        _clients = clients;
        _settings = settings;
        Owner = owner;
        Title = $"Temporary uploads - {clients.Count} computer(s)";
        Width = 1040;
        Height = 620;
        MinWidth = 760;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AddColumn("Relative path", nameof(StagedUploadRow.RelativePath), 300);
        AddColumn("Purpose", nameof(StagedUploadRow.Purpose), 90);
        AddColumn("Size(s)", nameof(StagedUploadRow.Size), 120);
        AddColumn("Available", nameof(StagedUploadRow.Available), 90);
        AddColumn("Computers", nameof(StagedUploadRow.Computers), 260);
        AddColumn("Newest", nameof(StagedUploadRow.Modified), 140);

        var refresh = Button("_Refresh", async (_, _) => await LoadAsync());
        _uploadButton = Button("_Add installers...", async (_, _) => await EnqueueUploadAsync());
        var install = Button("_Next: configure...", InstallSelected);
        var delete = Button("_Delete selected", async (_, _) => await DeleteSelectedAsync());
        var close = Button("_Close", (_, _) => Close());
        close.IsCancel = true;
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in new[] { refresh, _uploadButton, install, delete, close }) buttons.Children.Add(button);

        var note = Ui.Explanation(
            "Step 1 of 5 — Select an installer. Only this Host's opaque, DPAPI-protected upload namespace is shown. Upload installers here once, then choose a file that is present with the same size on every selected computer. Cleanup cannot cross into another Host's namespace.",
            new Thickness(0, 0, 0, 8));
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(note, Dock.Top);
        var activity = new StackPanel();
        activity.Children.Add(_status);
        activity.Children.Add(_progress);
        activity.Children.Add(_queueStatus);
        DockPanel.SetDock(activity, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(activity);
        root.Children.Add(buttons);
        var gridArea = new Grid();
        _emptyState.TextAlignment = TextAlignment.Center;
        _emptyState.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyState.VerticalAlignment = VerticalAlignment.Center;
        _emptyState.MaxWidth = 520;
        _emptyState.IsHitTestVisible = false;
        gridArea.Children.Add(_grid);
        gridArea.Children.Add(_emptyState);
        root.Children.Add(gridArea);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
        Ui.RefreshOnF5(this, LoadAsync);
    }

    void AddColumn(string header, string path, double width) =>
        _grid.Columns.Add(new DataGridTextColumn { Header = header, Width = width, Binding = new Binding(path) });

    Button Button(string content, RoutedEventHandler click)
    {
        var button = new Button { Content = content, MinWidth = 120, Margin = new Thickness(0, 0, 8, 8) };
        button.Click += click;
        _actionButtons.Add(button);
        return button;
    }

    async Task LoadAsync()
    {
        if (_running) return;
        SetRunning(true);
        _progress.Visibility = Visibility.Collapsed;
        _queueStatus.Visibility = Visibility.Collapsed;
        var errors = new ConcurrentBag<string>();
        try
        {
            _status.Text = "Reading temporary uploads...";
            using var gate = new SemaphoreSlim(8);
            var results = await Task.WhenAll(_clients.Select(async client =>
            {
                await gate.WaitAsync();
                try
                {
                    using var session = await ClientSession.OpenAsync(client, _settings);
                    var data = await session.CallAsync(Cmd.StagingList,
                        new JsonObject { ["namespace"] = StagingNamespace.Id(_settings) });
                    return data["files"]!.AsArray().Select(node =>
                    {
                        var file = node!.AsObject();
                        DateTimeOffset.TryParse(file["modifiedUtc"]?.GetValue<string>(), out var modified);
                        return new StagedUploadPresence(client,
                            file["relativePath"]?.GetValue<string>() ?? "",
                            file["size"]?.GetValue<long>() ?? 0, modified);
                    }).Where(file => file.RelativePath.Length > 0).ToList();
                }
                catch (Exception ex)
                {
                    errors.Add($"{client.Name}: {ex.Message}");
                    return [];
                }
                finally { gate.Release(); }
            }));

            var rows = results.SelectMany(value => value)
                .GroupBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var entries = group.OrderBy(value => value.Client.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    var sizes = entries.Select(value => value.Size).Distinct().Order().ToList();
                    var firstPart = group.Key.Split('\\', '/').FirstOrDefault() ?? "other";
                    return new StagedUploadRow
                    {
                        RelativePath = group.Key,
                        Purpose = firstPart,
                        Size = string.Join(" / ", sizes.Select(FormatSize)),
                        Available = $"{entries.Count} / {_clients.Count}",
                        Computers = string.Join(", ", entries.Select(value => value.Client.Name)),
                        Modified = entries.Max(value => value.ModifiedUtc).ToLocalTime().ToString("g"),
                        Entries = entries,
                    };
                })
                .OrderBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _grid.ItemsSource = rows;
            _emptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _status.Text = $"{rows.Count} staged path(s) across {_clients.Count - errors.Count} of {_clients.Count} computer(s)." +
                           (errors.IsEmpty ? "" : " Unavailable: " + string.Join("; ", errors));
        }
        finally { SetRunning(false); }
    }

    async Task EnqueueUploadAsync()
    {
        if (_running && !_uploading) return;
        var picker = new OpenFileDialog
        {
            Title = "Upload installers to the selected computers",
            Filter = "Installers (*.exe;*.msi)|*.exe;*.msi|Executable installers (*.exe)|*.exe|Windows Installer packages (*.msi)|*.msi",
            Multiselect = true,
        };
        if (picker.ShowDialog(this) != true) return;
        var totalBytes = picker.FileNames.Sum(path => new FileInfo(path).Length);
        var names = string.Join("\n", picker.FileNames.Select(path => $"• {Path.GetFileName(path)} ({FormatSize(new FileInfo(path).Length)})"));
        if (MessageBox.Show(this,
                $"Upload {picker.FileNames.Length} installer(s), {FormatSize(totalBytes)}, to {_clients.Count} computer(s)?\n\n{names}",
                "Confirm temporary upload", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;

        _uploadQueue.Enqueue(new StagedUploadBatch(picker.FileNames.ToArray(), totalBytes));
        UpdateQueueStatus();
        if (!_uploading) await ProcessUploadQueueAsync();
    }

    async Task ProcessUploadQueueAsync()
    {
        _uploading = true;
        SetRunning(true);
        _progress.Visibility = Visibility.Visible;
        _queueStatus.Visibility = Visibility.Visible;
        var failures = new ConcurrentBag<string>();
        try
        {
            while (_uploadQueue.TryDequeue(out var batch))
            {
                UpdateQueueStatus(batch);
                var operations = _clients.SelectMany(client => batch.Files.Select(file =>
                    new StagedUploadOperation(OperationKey(client, file), new FileInfo(file).Length))).ToList();
                var tracker = new StagedUploadProgressTracker(operations);
                _progress.Value = 0;
                var completed = 0;
                var total = operations.Count;
                using var gate = new SemaphoreSlim(4);
                await Task.WhenAll(_clients.Select(async client =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        ClientSession? session = null;
                        try { session = await ClientSession.OpenAsync(client, _settings); }
                        catch (Exception ex)
                        {
                            failures.Add($"{client.Name}: {ex.Message}");
                            foreach (var file in batch.Files)
                                ReportUploadProgress(tracker, client, file, 1, ref completed, total, completedOperation: true);
                            return;
                        }

                        using (session)
                        {
                            foreach (var file in batch.Files)
                            {
                                var relative = Path.Combine("install", Path.GetFileName(file));
                                var key = OperationKey(client, file);
                                var progress = new Progress<double>(fraction =>
                                    ReportUploadProgress(tracker, client, file, fraction, ref completed, total, completedOperation: false));
                                try
                                {
                                    await session.UploadIfDifferentSizeAsync(file,
                                        StagingNamespace.RemotePath(_settings, relative), progress);
                                }
                                catch (Exception ex) { failures.Add($"{client.Name} - {Path.GetFileName(file)}: {ex.Message}"); }
                                finally
                                {
                                    tracker.Report(key, 1);
                                    ReportUploadProgress(tracker, client, file, 1, ref completed, total, completedOperation: true);
                                }
                            }
                        }
                    }
                    finally { gate.Release(); }
                }));
            }
        }
        finally
        {
            _uploading = false;
            SetRunning(false);
            _queueStatus.Text = "Upload queue complete.";
        }
        await LoadAsync();
        if (!failures.IsEmpty)
            MessageBox.Show(this, string.Join("\n\n", failures), "Some uploads failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void ReportUploadProgress(StagedUploadProgressTracker tracker, ClientInfo client, string file,
        double fraction, ref int completed, int total, bool completedOperation)
    {
        var overall = tracker.Report(OperationKey(client, file), fraction);
        if (completedOperation) Interlocked.Increment(ref completed);
        _progress.Value = overall;
        _status.Text = $"Uploading {Path.GetFileName(file)} to {client.Name}: {fraction:P0}. " +
                       $"{Math.Min(completed, total)} of {total} file/computer operations complete; overall {overall:P0}.";
    }

    void UpdateQueueStatus(StagedUploadBatch? active = null)
    {
        var activeText = active is null ? "" :
            $"Active: {active.Files.Length} file(s), {FormatSize(active.TotalBytes)}. ";
        var queuedFiles = _uploadQueue.Sum(batch => batch.Files.Length);
        _queueStatus.Text = activeText +
            $"Queued: {_uploadQueue.Count} batch(es), {queuedFiles} file selection(s). Use Add installers to append another batch.";
        _queueStatus.Visibility = Visibility.Visible;
    }

    static string OperationKey(ClientInfo client, string file) =>
        $"{client.ClientId}\0{client.Address}\0{file}";

    async Task DeleteSelectedAsync()
    {
        if (_running) return;
        var selected = _grid.SelectedItems.Cast<StagedUploadRow>().ToList();
        if (selected.Count == 0) return;
        var operations = selected.SelectMany(row => row.Entries).ToList();
        if (MessageBox.Show(this,
                $"Permanently delete {selected.Count} staged path(s) in {operations.Count} computer location(s)?\n\n" +
                string.Join("\n", selected.Take(12).Select(row => row.RelativePath)) + (selected.Count > 12 ? "\n..." : ""),
                "Confirm staged-upload cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        SetRunning(true);
        var failures = new ConcurrentBag<string>();
        try
        {
            await Task.WhenAll(operations.GroupBy(value => value.Client).Select(async group =>
            {
                try
                {
                    using var session = await ClientSession.OpenAsync(group.Key, _settings);
                    foreach (var entry in group)
                        await session.CallAsync(Cmd.StagingDelete, new JsonObject
                        {
                            ["namespace"] = StagingNamespace.Id(_settings),
                            ["relativePath"] = entry.RelativePath,
                        });
                }
                catch (Exception ex) { failures.Add($"{group.Key.Name}: {ex.Message}"); }
            }));
        }
        finally { SetRunning(false); }
        await LoadAsync();
        if (!failures.IsEmpty)
            MessageBox.Show(this, string.Join("\n\n", failures), "Some files were not deleted", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void InstallSelected(object sender, RoutedEventArgs e)
    {
        if (_grid.SelectedItems.Cast<StagedUploadRow>().ToList() is not [var row])
        {
            MessageBox.Show(this, "Select exactly one staged installer.");
            return;
        }
        if (!row.CanInstallOnAll(_clients.Count))
        {
            MessageBox.Show(this, "Select an .exe or .msi installer that exists with the same size on every selected computer.",
                "Installer not synchronized", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SelectedInstallRelativePath = row.RelativePath;
        DialogResult = true;
    }

    void SetRunning(bool running)
    {
        _running = running;
        foreach (var button in _actionButtons) button.IsEnabled = !running;
        if (_uploading) _uploadButton.IsEnabled = true;
    }

    public static string FormatSize(long value) => value < 1024 ? $"{value} B" :
        value < 1048576 ? $"{value / 1024d:0.0} KB" : value < 1073741824 ? $"{value / 1048576d:0.0} MB" :
        $"{value / 1073741824d:0.0} GB";

    sealed record StagedUploadBatch(string[] Files, long TotalBytes);
}
