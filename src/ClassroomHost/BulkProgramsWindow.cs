// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Compares installed software across selected computers and safely uninstalls matched entries in batches.</summary>
public sealed class BulkProgramsWindow : Window
{
    const string MachineLevel = "Machine-wide (elevated)";
    const string UserLevel = "Signed-in user (standard)";
    const string AllPrograms = SoftwareInventoryPresentation.AllPrograms;
    const string DifferencesOnly = SoftwareInventoryPresentation.DifferencesOnly;
    const string VersionDifferences = SoftwareInventoryPresentation.VersionDifferences;
    const string MissingPrograms = SoftwareInventoryPresentation.MissingPrograms;

    readonly IReadOnlyList<ClientInfo> _clients;
    readonly HostSettings _settings;
    readonly BulkOperationRunner _runOperation;
    readonly ProgramWindowActions? _actions;
    readonly ComboBox _level = new() { ItemsSource = new[] { MachineLevel, UserLevel }, SelectedIndex = 0, Width = 205 };
    readonly ComboBox _comparison = new() { ItemsSource = SoftwareInventoryPresentation.Filters, DisplayMemberPath = nameof(SoftwareInventoryPresentation.FilterChoice.Shown), SelectedValuePath = nameof(SoftwareInventoryPresentation.FilterChoice.Value), SelectedIndex = 0, Width = 175 };
    readonly TextBox _filter = new() { Width = 190 };
    readonly TextBox _args = new() { MinWidth = 260 };
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
    readonly ProgressBar _progress = new()
    {
        Minimum = 0,
        Maximum = 1,
        Height = 8,
        Margin = new Thickness(0, 6, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    readonly Button _refresh;
    readonly Button _uninstall;
    List<ComparedProgram> _all = [];
    bool _running;
    int _loadVersion;

    public BulkProgramsWindow(IReadOnlyList<ClientInfo> clients, HostSettings settings, Window owner,
        BulkOperationRunner runOperation, ProgramWindowActions? programActions = null, string initialFilter = "")
    {
        _clients = clients;
        _actions = programActions;
        _settings = settings;
        _runOperation = runOperation;
        Owner = owner;
        Title = $"Programs comparison - {clients.Count} computers";
        Width = 1180;
        Height = 680;
        MinWidth = 820;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var saved = settings.FilterState(nameof(BulkProgramsWindow));
        _filter.Text = initialFilter.Length > 0 ? initialFilter : saved.Text;
        SelectSaved(_level, saved.Selection("Scope", MachineLevel));
        _comparison.SelectedValue = saved.Selection("Comparison", AllPrograms);
        if (_comparison.SelectedIndex < 0) _comparison.SelectedIndex = 0;
        UiTranslator.SetPreserveText(_comparison, true);
        UiTranslator.SetPreserveText(_grid, true);

        AddColumn("Program", nameof(ComparedProgram.Name), 250);
        AddColumn("Version(s)", nameof(ComparedProgram.Versions), 150);
        AddColumn("Installed", nameof(ComparedProgram.Installed), 105);
        AddColumn("Publisher", nameof(ComparedProgram.Publishers), 170);
        AddColumn("Silent", nameof(ComparedProgram.Silent), 75);
        AddColumn("Computer versions", nameof(ComparedProgram.Computers), 250);
        AddColumn("Missing from", nameof(ComparedProgram.MissingComputers), 170);
        AddColumn("Comparison", nameof(ComparedProgram.Comparison), 190);
        TableColumns.Apply(_grid, saved.HiddenColumns);

        _refresh = ActionButton("_Recheck", "\u21BB", "Reload installed software from every selected computer.");
        _refresh.Click += async (_, _) => await LoadAsync();
        var columns = ActionButton("C_olumns...", "☰", "Columns — choose which comparison columns are visible.");
        columns.Click += (_, _) =>
        {
            if (TableColumns.Choose(this, "Programs comparison columns", _grid, saved)) _settings.Save();
        };
        _uninstall = ActionButton("_Uninstall selected", "\uD83D\uDDD1", "Batch uninstall selected products from every checked computer where they are installed.");
        _uninstall.Click += async (_, _) => await UninstallAsync();
        var close = ActionButton("_Close", "\u2715", "Close the comparison window.");
        close.IsCancel = true;
        close.Click += (_, _) => Close();

        _level.SelectionChanged += async (_, _) => { if (IsLoaded) await LoadAsync(); };
        _comparison.SelectionChanged += (_, _) => ApplyFilter();
        _filter.TextChanged += (_, _) => ApplyFilter();

        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        controls.Children.Add(Ui.Label("_Scope:", _level));
        controls.Children.Add(_level);
        controls.Children.Add(Ui.Label("_Show:", _comparison, new Thickness(12, 0, 6, 0)));
        controls.Children.Add(_comparison);
        controls.Children.Add(Ui.Label("_Filter:", _filter, new Thickness(12, 0, 6, 0)));
        controls.Children.Add(_filter);
        controls.Children.Add(_refresh);
        controls.Children.Add(columns);

        var note = Ui.Explanation(
            "Select one or more product rows to remove them from every checked computer where found. " +
            "Silent shows trusted silent entries / total registry entries; entries without a trusted silent command are skipped unless custom switches are supplied.",
            new Thickness(0, 0, 0, 8));

        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Ui.Label("Custom silent s_witches:", _args));
        actions.Children.Add(_args);
        if (_actions is { } hooks)
        {
            var open = ActionButton("O_pen selected...", "\u25B6", "Open selected — start the selected program for the signed-in users of the computers where it is installed.");
            open.Click += async (_, _) => await OpenSelectedAsync();
            var install = ActionButton("_Install another program...", "\uD83D\uDCE6", "Install another program — open the install dialog for the computers of this comparison.");
            install.Click += (_, _) => hooks.Install(_clients);
            actions.Children.Add(open);
            actions.Children.Add(install);
        }
        actions.Children.Add(_uninstall);
        actions.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(controls, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(_progress, Dock.Bottom);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(controls);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(_progress);
        root.Children.Add(actions);
        root.Children.Add(_grid);
        Content = root;
        ModuleUiPolicy.WatchVisibility(this, settings, BuiltInModules.Software,
            [controls, note, _grid, .. actions.Children.OfType<UIElement>().Where(control => !ReferenceEquals(control, close))]);

        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, _) =>
        {
            saved.Text = _filter.Text;
            saved.Selections["Scope"] = _level.SelectedItem as string ?? MachineLevel;
            saved.Selections["Comparison"] = _comparison.SelectedValue as string ?? AllPrograms;
            _settings.Save();
        };
        Ui.RefreshOnF5(this, LoadAsync);
    }

    string Level => _level.SelectedIndex == 1 ? Levels.User : Levels.System;

    /// <summary>Opens one selected product on the computers that have it.</summary>
    async Task OpenSelectedAsync()
    {
        if (!BuiltInModules.Enabled(_settings, BuiltInModules.Software)) return;
        if (_actions is null) return;
        if (_grid.SelectedItems.Count != 1 || _grid.SelectedItem is not ComparedProgram row)
        {
            MessageBox.Show(this, "Select one program to open.", "Open selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var targets = row.Entries.Select(entry => entry.Client).Distinct().ToList();
        await _actions.OpenProgram(targets, ProgramLaunch.Suggestion(row.Entries.Select(entry => entry.Program)));
    }

    static void SelectSaved(ComboBox box, string value)
    {
        if (box.Items.Contains(value)) box.SelectedItem = value;
    }

    void AddColumn(string header, string path, double width)
    {
        var column = new DataGridTextColumn
        {
            Width = width,
            Binding = new Binding(path),
        };
        UiTranslator.SetColumnHeader(column, header);
        _grid.Columns.Add(column);
    }

    static Button ActionButton(string content, string icon, string tooltip) => new()
    {
        Content = content,
        Tag = icon,
        ToolTip = tooltip,
        MinWidth = 110,
    };

    async Task LoadAsync()
    {
        if (_running) return;
        if (!BuiltInModules.Enabled(_settings, BuiltInModules.Software))
        {
            _all = [];
            ApplyFilter();
            _status.Text = BuiltInModules.Disabled;
            return;
        }
        var version = ++_loadVersion;
        SetRunning(true);
        _progress.Visibility = Visibility.Collapsed;
        try
        {
            var completed = 0;
            _status.Text = $"Checking installed software on {_clients.Count} computers...";
            using var gate = new SemaphoreSlim(8);
            var results = await Task.WhenAll(_clients.Select(async client =>
            {
                await gate.WaitAsync();
                try { return await LoadOneAsync(client); }
                finally
                {
                    gate.Release();
                    var count = Interlocked.Increment(ref completed);
                    await Dispatcher.InvokeAsync(() =>
                        _status.Text = $"Checked {count} of {_clients.Count} computers...");
                }
            }));
            if (version != _loadVersion) return;

            var successful = results.Where(result => result.Error.Length == 0)
                .ToDictionary(result => result.Client, result => (IReadOnlyList<ProgramEntry>)result.Programs);
            _all = ProgramInventoryComparison.Build(_clients, successful);
            ApplyFilter();
            var failures = results.Where(result => result.Error.Length > 0).ToList();
            _status.Text = $"Compared {_all.Count} products across {successful.Count} of {_clients.Count} computers." +
                (failures.Count == 0 ? "" : " Unavailable: " + string.Join("; ", failures.Select(result => $"{result.Client.Name}: {result.Error}")));
        }
        finally { SetRunning(false); }
    }

    async Task<ProgramInventoryResult> LoadOneAsync(ClientInfo client)
    {
        try
        {
            if (client.ExclusiveOperation.Length > 0)
                return new(client, [], client.ExclusiveOperation);
            using var session = await ClientSession.OpenAsync(client, _settings);
            var (software, machine) = ModuleRuns.OpenSoftware(session, client);
            var data = JsonNode.Parse(await software.ListAsync(machine, Level == Levels.System, CancellationToken.None))!.AsObject();
            var programs = data["programs"]!.AsArray()
                .Select(item => ProgramEntry.FromJson(item!.AsObject())).ToList();
            return new(client, programs, "");
        }
        catch (Exception ex) { return new(client, [], ex.Message); }
    }

    void ApplyFilter()
    {
        var text = _filter.Text.Trim();
        var mode = _comparison.SelectedValue as string ?? AllPrograms;
        var shown = _all.Where(item =>
            FilterMatch.Matches(text, item.Name, item.Publishers) &&
            (mode == AllPrograms ||
             mode == DifferencesOnly && item.HasDifference ||
             mode == VersionDifferences && item.HasVersionDifference ||
             mode == MissingPrograms && item.IsMissingFromSome)).ToList();
        TableSorting.SetItemsPreservingSort(_grid, shown);
        if (!_running) _status.Text = $"Showing {shown.Count} of {_all.Count} compared products across {_clients.Count} selected computers.";
    }

    async Task UninstallAsync()
    {
        if (_running || !BuiltInModules.Enabled(_settings, BuiltInModules.Software)) return;
        var products = _grid.SelectedItems.Cast<ComparedProgram>().ToList();
        if (products.Count == 0)
        {
            MessageBox.Show(this, "Select one or more product rows first.", "Batch uninstall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var customArgs = _args.Text.Trim();
        var allEntries = products.SelectMany(product => product.Entries)
            .DistinctBy(entry => $"{entry.Client.Address}\0{entry.Program.View}\0{entry.Program.Key}", StringComparer.OrdinalIgnoreCase)
            .ToList();
        var runnable = allEntries.Where(entry => entry.Program.Silent || customArgs.Length > 0).ToList();
        var skipped = allEntries.Count - runnable.Count;
        if (runnable.Count == 0)
        {
            MessageBox.Show(this,
                "None of the selected registry entries advertises a trusted silent uninstall command. " +
                "Enter publisher-documented silent switches, or test the program on one computer in the single-computer Programs window.",
                "Batch uninstall", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var targetCount = runnable.Select(entry => entry.Client).Distinct().Count();
        var names = string.Join("\n", products.Take(10).Select(product => $"• {product.Name} ({product.Versions})")) +
                    (products.Count > 10 ? "\n• ..." : "");
        var operationTargets = runnable.Select(entry => entry.Client).Distinct().ToList();
        var targetText = OperationTargetSummary.Format(operationTargets);
        var review = new BulkOperationReview(
            "Review software uninstall",
            targetText.Heading + "\n" + targetText.Detail,
            [
                new("Products", names),
                new("Operations", $"{runnable.Count} uninstall operation(s) across {targetCount} computer(s)"),
                new("Silent parameters", customArgs.Length == 0 ? "Use each product's trusted registry command" : customArgs),
                new("Skipped", skipped == 0 ? "None" : $"{skipped} installation(s) without a trusted silent command"),
            ],
            "Step 3 of 5 — Review before starting. The progress bar counts every completed, failed, unavailable, or skipped operation; per-computer results stay in Operations in the main window.",
            "Review the product versions carefully. Uninstall cannot be undone.",
            "Uninstall");
        if (new BulkOperationReviewWindow(this, review).Ask() != BulkOperationReviewDecision.Run) return;

        SetRunning(true);
        _progress.Maximum = runnable.Count;
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;
        var level = Level;
        var entriesByClient = runnable.GroupBy(entry => entry.Client).ToDictionary(group => group.Key, group => group.ToList());
        var countedByClient = operationTargets.ToDictionary(client => client, _ => 0);
        var failures = new List<string>();
        var completed = 0;
        // The operation runner resumes on the UI thread, so progress and results need no locking.
        void ReportCompleted(ClientInfo client, int amount)
        {
            countedByClient[client] += amount;
            completed += amount;
            _progress.Value = completed;
            _status.Text = $"Completed {completed} of {runnable.Count} uninstall operations. Per-computer results are in Operations in the main window.";
        }
        try
        {
            await _runOperation(operationTargets, ProgramUninstallOutcome.Title(products.Select(product => product.Name).ToList()),
                async (client, session) =>
                {
                    var entries = entriesByClient[client];
                    var failed = new List<string>();
                    var (software, machine) = ModuleRuns.OpenSoftware(session, client);
                    foreach (var entry in entries)
                    {
                        try
                        {
                            await software.UninstallAsync(machine, level == Levels.System, entry.Program.Key, entry.Program.View, customArgs, CancellationToken.None);
                        }
                        catch (Exception ex) { failed.Add($"{entry.Program.Name}: {ex.Message}"); }
                        finally { ReportCompleted(client, 1); }
                    }
                    var message = ProgramUninstallOutcome.FailureMessage(entries.Count, failed);
                    if (message.Length > 0) throw new InvalidOperationException(message);
                },
                4,
                skipped == 0 ? "" : $"{skipped} installation(s) without a trusted silent command were skipped.",
                (client, error) =>
                {
                    // Unreachable, busy, and cancelled computers never reach the per-product loop.
                    ReportCompleted(client, entriesByClient[client].Count - countedByClient[client]);
                    if (error.Length > 0) failures.Add($"{client.Name}: {error}");
                });
        }
        finally { SetRunning(false); }

        await LoadAsync();
        if (failures.Count > 0)
            MessageBox.Show(this, string.Join("\n\n", failures.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)) +
                "\n\nPer-computer results remain in Operations in the main window.",
                "Some uninstalls failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void SetRunning(bool value)
    {
        _running = value;
        _refresh.IsEnabled = !value;
        _uninstall.IsEnabled = !value;
        _level.IsEnabled = !value;
    }

    sealed record ProgramInventoryResult(ClientInfo Client, List<ProgramEntry> Programs, string Error);
}
