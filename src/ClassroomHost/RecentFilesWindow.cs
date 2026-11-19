// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>One file or folder in the Recent files list.</summary>
public sealed class RecentFileRow : INotifyPropertyChanged
{
    public required ClientInfo Client { get; init; }
    public required RecentEntry Entry { get; init; }
    /// <summary>The type in English (a group name, Folders or Other files), as the filters compare it.</summary>
    public required string Kind { get; init; }
    public string KindShown => Loc.T(Kind);

    public string Computer => Client.Name;
    public string Name => Path.GetFileName(Entry.Path);
    /// <summary>The watched folder and the subfolder, as "Desktop\Project".</summary>
    public string Folder
    {
        get
        {
            var sub = Path.GetDirectoryName(Entry.RelativePath) ?? "";
            var label = Loc.T(RecentFilesPlan.FolderLabel(Entry.Root));
            return sub.Length == 0 ? label : Path.Combine(label, sub);
        }
    }
    public string Changed => Entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public string Size => Entry.IsFolder ? "" : StagedUploadsWindow.FormatSize(Entry.Size);

    string _status = "";
    /// <summary>What happened to it, in English; <see cref="StatusShown"/> is what the table shows.</summary>
    public string Status
    {
        get => _status;
        set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusShown))); }
    }
    public string StatusShown => Loc.T(_status);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Recent files (T-212): what the signed-in students changed in the watched folders of the selected computers in a time window, by
/// kind, and copies of them on this PC, one folder per computer.
/// </summary>
public sealed class RecentFilesWindow : Window
{
    readonly IReadOnlyList<ClientInfo> _targets;
    readonly HostSettings _settings;
    RecentFilesSettings Recent => _settings.RecentFiles;

    readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended,
        SelectionUnit = DataGridSelectionUnit.FullRow, CanUserAddRows = false, CanUserDeleteRows = false,
    };
    readonly ComboBox _window = new() { MinWidth = 150, Margin = new Thickness(0, 0, 8, 0) };
    readonly DatePicker _from = new() { Width = 120, Margin = new Thickness(0, 0, 4, 0) };
    readonly TextBox _fromTime = new() { Width = 52, Text = "08:00", Margin = new Thickness(0, 0, 8, 0) };
    readonly DatePicker _to = new() { Width = 120, Margin = new Thickness(0, 0, 4, 0) };
    readonly TextBox _toTime = new() { Width = 52, Text = "23:59", Margin = new Thickness(0, 0, 8, 0) };
    readonly StackPanel _custom = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    readonly WrapPanel _kinds = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly ComboBox _destination = new() { IsEditable = true, MinWidth = 320 };
    readonly CheckBox _overwrite = new() { Content = "_Overwrite earlier copies", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly TextBox _keepDays = new() { Width = 44, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _status = Ui.StatusText(new Thickness(0, 6, 0, 0));
    /// <summary>One line per computer or file that had a problem, each its own sentence so it is translated on its own.</summary>
    readonly StackPanel _problems = new();
    readonly List<Button> _busyButtons = new();
    readonly List<RecentFileRow> _rows = new();
    readonly CancellationTokenSource _closing = new();
    /// <summary>The token of <see cref="_closing"/>, taken once: it is cancelled before the source is disposed, so it stays safe to use.</summary>
    readonly CancellationToken _stop;
    bool _running;

    public RecentFilesWindow(IReadOnlyList<ClientInfo> targets, HostSettings settings, Window owner)
    {
        _targets = targets;
        _settings = settings;
        _stop = _closing.Token;
        Owner = owner;
        Title = targets.Count == 1 ? $"Recent files - {targets[0].Name}" : $"Recent files - {targets.Count} computers";
        Width = 1100;
        Height = 660;
        MinWidth = 820;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AddColumn("Computer", nameof(RecentFileRow.Computer), 120);
        AddColumn("Name", nameof(RecentFileRow.Name), 230);
        AddColumn("Folder", nameof(RecentFileRow.Folder), 200);
        AddColumn("Type", nameof(RecentFileRow.KindShown), 100);
        AddColumn("Changed", nameof(RecentFileRow.Changed), 125);
        AddColumn("Size", nameof(RecentFileRow.Size), 80);
        AddColumn("Copy", nameof(RecentFileRow.StatusShown), 170);
        _grid.ItemsSource = _rows;
        _grid.MouseDoubleClick += async (_, _) => await CopyAsync(selectedOnly: true);

        var note = Ui.Explanation(
            "Lists the files the signed-in students changed in the watched folders (Desktop and Documents unless you choose others for a computer), " +
            "and copies them to this PC, one folder per computer. The students' files stay where they are, and the student computers keep no list of " +
            "them: each Refresh looks at the folders at that moment. The copies stay on this PC for the days you choose and are then deleted, unless you changed them.",
            new Thickness(0, 0, 0, 8));

        // Time window, then the kinds to list.
        foreach (var window in RecentFilesPlan.Windows) _window.Items.Add(window.Name);
        _window.SelectedItem = RecentFilesPlan.Windows.Any(window => window.Name == Recent.Window) ? Recent.Window : RecentFilesPlan.DefaultWindow;
        _window.SelectionChanged += (_, _) => _custom.Visibility = IsCustom ? Visibility.Visible : Visibility.Collapsed;
        _from.SelectedDate = DateTime.Today;
        _to.SelectedDate = DateTime.Today;
        _custom.Children.Add(Ui.Label("_From", _from, new Thickness(0, 0, 4, 0)));
        _custom.Children.Add(_from);
        _custom.Children.Add(_fromTime);
        _custom.Children.Add(Ui.Label("_To", _to, new Thickness(0, 0, 4, 0)));
        _custom.Children.Add(_to);
        _custom.Children.Add(_toTime);
        _custom.Visibility = IsCustom ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(_fromTime, "From time");
        System.Windows.Automation.AutomationProperties.SetName(_toTime, "To time");

        var refresh = BusyButton("_Refresh", async (_, _) => await RefreshAsync());
        var timeRow = new WrapPanel();
        timeRow.Children.Add(Ui.Label("C_hanged in", _window, new Thickness(0, 0, 6, 0)));
        timeRow.Children.Add(_window);
        timeRow.Children.Add(_custom);
        timeRow.Children.Add(refresh);
        timeRow.Children.Add(IconBusyButton("_Watched folders...", "📁", (_, _) => EditWatchedFolders()));
        timeRow.Children.Add(IconBusyButton("Manage t_ypes...", "☰", (_, _) => ManageTypes()));
        BuildKinds();

        // Where copies go, and the copy buttons.
        foreach (var (value, _) in CollectFolders.OnThisComputer(Recent.Destination.Length > 0 ? [Recent.Destination] : []))
            _destination.Items.Add(value);
        _destination.Text = Destination;
        System.Windows.Automation.AutomationProperties.SetName(_destination, "Save into");
        var browse = new Button { Content = "_Browse...", Margin = new Thickness(6, 0, 12, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Save the copies into" };
            if (Directory.Exists(_destination.Text.Trim())) dlg.InitialDirectory = _destination.Text.Trim();
            if (dlg.ShowDialog(this) == true) _destination.Text = dlg.FolderName;
        };
        _overwrite.IsChecked = Recent.Overwrite;
        _keepDays.Text = Recent.KeepDays.ToString(CultureInfo.InvariantCulture);
        System.Windows.Automation.AutomationProperties.SetName(_keepDays, "Keep copies for (days)");
        var saveRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        saveRow.Children.Add(Ui.Label("Save _into", _destination, new Thickness(0, 0, 6, 0)));
        saveRow.Children.Add(_destination);
        saveRow.Children.Add(browse);
        var createFolder = new Button { Content = "Create new folder", Margin = new Thickness(0, 0, 12, 0) };
        createFolder.Click += (_, _) => LocalFolderCreation.Prompt(this, () => _destination.Text, path => _destination.Text = path);
        saveRow.Children.Add(createFolder);
        saveRow.Children.Add(_overwrite);
        saveRow.Children.Add(Ui.Label("_Keep copies for (days, 0 = always)", _keepDays, new Thickness(0, 0, 6, 0)));
        saveRow.Children.Add(_keepDays);

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(BusyButton("Copy _selected", async (_, _) => await CopyAsync(selectedOnly: true)));
        buttons.Children.Add(BusyButton("Copy _all listed", async (_, _) => await CopyAsync(selectedOnly: false)));
        var open = new Button { Content = "Op_en folder", Margin = new Thickness(0, 0, 8, 0) };
        open.Click += (_, _) => OpenDestination();
        buttons.Children.Add(open);
        var close = new Button { Content = "_Close", IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        Ui.Spaced(timeRow);
        Ui.Spaced(buttons);

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(note);
        top.Children.Add(timeRow);
        top.Children.Add(_kinds);
        var bottom = new StackPanel();
        bottom.Children.Add(saveRow);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);
        bottom.Children.Add(new ScrollViewer { Content = _problems, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_grid);
        Content = root;
        Ui.NameLists(this, "Recent files");
        Ui.RefreshOnF5(this, RefreshAsync);

        Closed += (_, _) =>
        {
            _closing.Cancel();
            SaveChoices();
            // Runs still going end at their next step and see the cancel; nothing waits on the token after this.
            _closing.Dispose();
        };
        Loaded += async (_, _) =>
        {
            PruneOldCopies();
            await RefreshAsync();
        };
    }

    bool IsCustom => (_window.SelectedItem as string) == RecentFilesPlan.CustomWindow;

    string Destination => Recent.Destination.Length > 0 ? Recent.Destination : RecentFilesPlan.DefaultDestination();

    IReadOnlyCollection<string> CheckedKinds =>
        _kinds.Children.OfType<CheckBox>().Where(box => box.IsChecked == true).Select(box => (string)box.Tag).ToList();

    IReadOnlyList<FileTypeGroup> Groups => Recent.TypeGroups.Select(group => group.ToGroup()).ToList();

    void AddColumn(string header, string path, double width) =>
        _grid.Columns.Add(new DataGridTextColumn { Header = header, Width = width, Binding = new Binding(path) });

    Button BusyButton(string content, RoutedEventHandler click)
    {
        var button = new Button { Content = content, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += click;
        _busyButtons.Add(button);
        return button;
    }

    /// <summary>A button with its own icon (a symbol FluentIconConverter knows) that is off while a run is going on.</summary>
    Button IconBusyButton(string content, string icon, RoutedEventHandler click)
    {
        var button = BusyButton(content, click);
        button.Tag = icon;
        return button;
    }

    void BuildKinds()
    {
        _kinds.Children.Clear();
        _kinds.Children.Add(new TextBlock { Text = "Types:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (var kind in RecentFilesPlan.Kinds(Recent.TypeGroups))
        {
            var group = Recent.TypeGroups.FirstOrDefault(item => item.Name == kind);
            _kinds.Children.Add(new CheckBox
            {
                Content = kind, Tag = kind, IsChecked = Recent.CheckedKinds.Contains(kind), Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = group is not null ? string.Join(" ", group.Extensions)
                    : kind == FileTypes.Folders ? "Folders changed in the time window; copying one copies everything in it."
                    : "Files whose type is in none of the groups.",
            });
        }
    }

    void SetRunning(bool running)
    {
        _running = running;
        foreach (var button in _busyButtons) button.IsEnabled = !running;
        // The problems of the last run go when the next one starts.
        if (running) _problems.Children.Clear();
    }

    /// <summary>The window's choices, kept for next time.</summary>
    void SaveChoices()
    {
        Recent.Window = _window.SelectedItem as string ?? RecentFilesPlan.DefaultWindow;
        Recent.CheckedKinds = CheckedKinds.ToList();
        Recent.Overwrite = _overwrite.IsChecked == true;
        if (int.TryParse(_keepDays.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days)) Recent.KeepDays = Math.Min(days, 3650);
        var typed = PathText.Clean(_destination.Text);
        Recent.Destination = typed.Equals(RecentFilesPlan.DefaultDestination(), StringComparison.OrdinalIgnoreCase) ? "" : typed;
        _settings.Save();
    }

    async Task RefreshAsync()
    {
        if (_running) return;
        var kinds = CheckedKinds;
        if (kinds.Count == 0) { _status.Text = "Tick at least one type."; return; }
        var window = RecentFilesPlan.Windows.First(item => item.Name == (_window.SelectedItem as string ?? RecentFilesPlan.DefaultWindow));
        var (since, until, minutes) = RecentFilesPlan.Range(window, DateTime.Now, At(_from, _fromTime), At(_to, _toTime));
        var groups = Groups;

        SetRunning(true);
        _status.Text = $"Looking at {_targets.Count} computer(s)...";
        var found = new ConcurrentBag<RecentFileRow>();
        var problems = new ConcurrentBag<string>();
        try
        {
            using var gate = new SemaphoreSlim(6);
            await Task.WhenAll(_targets.Select(async client =>
            {
                await gate.WaitAsync(_stop);
                try
                {
                    if (!client.SupportsCommand(Cmd.FsRecent))
                    {
                        problems.Add($"{client.Name}: update the Client to protocol {RecentFiles.RequiredVersion} first.");
                        return;
                    }
                    var query = RecentFilesPlan.Query(RecentFilesPlan.WatchedFolders(Recent, client), Recent.TypeGroups, kinds, since, until, minutes);
                    using var session = await ClientSession.OpenAsync(client, _settings, _stop);
                    var scan = await RecentFilesCopier.ListAsync(session, query, _stop);
                    foreach (var entry in scan.Items.Where(entry => RecentFilesPlan.Wanted(entry, groups, kinds)))
                        found.Add(new RecentFileRow { Client = client, Entry = entry, Kind = RecentFilesPlan.Kind(entry, groups) });
                    foreach (var missing in scan.Missing) problems.Add($"{client.Name}: the folder {missing} does not exist.");
                    if (scan.Truncated) problems.Add($"{client.Name}: only the newest {scan.Items.Count} are listed.");
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (Exception ex) { problems.Add($"{client.Name}: {ErrorClassifier.Describe(ex)}"); }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
        finally { if (!_stop.IsCancellationRequested) SetRunning(false); }

        _rows.Clear();
        _rows.AddRange(found.OrderByDescending(row => row.Entry.ModifiedUtc).ThenBy(row => row.Computer, StringComparer.OrdinalIgnoreCase));
        _grid.Items.Refresh();
        var computers = _rows.Select(row => row.Client).Distinct().Count();
        _status.Text = $"{_rows.Count} file(s) and folder(s) on {computers} of {_targets.Count} computer(s).";
        ShowProblems(problems);
    }

    /// <summary>Lists the problems under the status line, the first few of them; the rest are counted.</summary>
    void ShowProblems(IEnumerable<string> problems)
    {
        const int shown = 8;
        var all = problems.Order(StringComparer.OrdinalIgnoreCase).ToList();
        _problems.Children.Clear();
        foreach (var problem in all.Take(shown))
            _problems.Children.Add(Ui.MutedText(problem));
        if (all.Count > shown) _problems.Children.Add(Ui.MutedText($"{all.Count - shown} more problem(s) are not shown."));
    }

    static DateTime? At(DatePicker date, TextBox time)
    {
        if (date.SelectedDate is not { } day) return null;
        return TimeSpan.TryParseExact(time.Text.Trim(), ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out var at) && at < TimeSpan.FromDays(1)
            ? day.Date + at : day.Date;
    }

    async Task CopyAsync(bool selectedOnly)
    {
        if (_running) return;
        var rows = (selectedOnly ? _grid.SelectedItems.Cast<RecentFileRow>() : _rows).ToList();
        if (rows.Count == 0)
        {
            _status.Text = selectedOnly ? "Select the files to copy (Ctrl and Shift select several), or use Copy all listed." : "Nothing is listed.";
            return;
        }
        var destination = PathText.Clean(_destination.Text);
        if (!Path.IsPathFullyQualified(destination)) { _status.Text = "Choose a folder on this PC to save into."; return; }
        SaveChoices();
        bool overwrite = _overwrite.IsChecked == true;

        SetRunning(true);
        int copied = 0, upToDate = 0;
        var failed = new ConcurrentBag<string>();
        try
        {
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(rows.GroupBy(row => row.Client).Select(async group =>
            {
                await gate.WaitAsync(_stop);
                var client = group.Key;
                try
                {
                    foreach (var row in group) row.Status = "Copying...";
                    using var session = await ClientSession.OpenAsync(client, _settings, _stop);
                    // A folder brings what is in it, so a file or subfolder of a copied folder is not sent a second time.
                    var entries = RecentFilesPlan.WithoutNested(group.Select(row => row.Entry));
                    var progress = new Progress<string>(text => _status.Text = text);
                    var outcome = await RecentFilesCopier.CopyAsync(session, client, entries, destination, overwrite, progress, _stop);
                    copied += outcome.Copied;
                    upToDate += outcome.UpToDate;
                    foreach (var problem in outcome.Failed) failed.Add($"{client.Name}: {problem}");
                    foreach (var (local, modified) in outcome.Written) RecentFilesPlan.Remember(Recent, local, DateTime.UtcNow, modified);
                    foreach (var row in group)
                        row.Status = !outcome.Failed.Any(problem => problem.StartsWith(row.Entry.Path, StringComparison.OrdinalIgnoreCase)) ? "Copied"
                            : row.Entry.IsFolder ? "Not all copied" : "Not copied";
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    var message = ErrorClassifier.Describe(ex);
                    failed.Add($"{client.Name}: {message}");
                    foreach (var row in group.Where(row => row.Status is "Waiting" or "Copying...")) row.Status = "Error: " + message;
                }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
        finally { if (!_stop.IsCancellationRequested) SetRunning(false); }

        _settings.Save();
        _status.Text = $"Copied {copied} file(s) into {destination}; {upToDate} were already up to date.";
        ShowProblems(failed);
    }

    void EditWatchedFolders()
    {
        var first = _targets[0];
        var current = string.Join(Environment.NewLine, RecentFilesPlan.WatchedFolders(Recent, first));
        var dlg = new FormDialog("Watched folders", this)
            .Target(_targets)
            .Note("One folder per line: %DESKTOP%, %DOCUMENTS%, %DOWNLOADS% and %USERPROFILE% are the signed-in student's own folders; " +
                  "any other folder is written as its full path. Leave it empty to watch Desktop and Documents.")
            .Text("folders", "_Folders", current, multiline: true);
        if (_targets.Count > 1) dlg.Note("The list is saved for each of the selected computers.");
        if (!dlg.Ask("Save")) return;
        foreach (var client in _targets) RecentFilesPlan.SetWatchedFolders(Recent, client, dlg.Raw("folders"));
        _settings.Save();
        _ = RefreshAsync();
    }

    void ManageTypes()
    {
        var dlg = new FormDialog("Manage file types", this)
            .Note("The extensions of each type, separated by spaces or commas (for example: docx pdf txt). A file whose type is in no group counts as Other files.");
        foreach (var group in Recent.TypeGroups)
            dlg.Text("group:" + group.Name, group.Name, string.Join(" ", group.Extensions));
        dlg.Check("reset", "_Put back the types Classroom Manager starts with");
        if (!dlg.Ask("Save")) return;
        if (dlg.B("reset")) Recent.TypeGroups = RecentFilesPlan.DefaultGroups();
        else
            foreach (var group in Recent.TypeGroups)
                group.Extensions = FileTypes.ParseExtensions(dlg.Raw("group:" + group.Name));
        Recent.CheckedKinds = CheckedKinds.ToList();
        BuildKinds();
        _settings.Save();
        _ = RefreshAsync();
    }

    void PruneOldCopies()
    {
        try
        {
            var removed = RecentFilesCopier.Prune(Recent, Destination, DateTime.UtcNow);
            if (removed > 0) { _settings.Save(); _status.Text = $"{removed} copied file(s) older than {Recent.KeepDays} days were deleted."; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    void OpenDestination()
    {
        var folder = PathText.Clean(_destination.Text);
        if (_targets.Count == 1 && Directory.Exists(RecentFilesPlan.ComputerFolder(folder, _targets[0])))
            folder = RecentFilesPlan.ComputerFolder(folder, _targets[0]);
        if (!Directory.Exists(folder)) { _status.Text = "Nothing has been copied into that folder yet."; return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
