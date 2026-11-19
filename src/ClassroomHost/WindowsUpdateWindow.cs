// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public static class WindowsUpdatePausePeriods
{
    public static readonly string[] Choices = ["1 day", "7 days", "14 days", "21 days", "28 days", "35 days"];

    public static int ParseDays(string choice) =>
        int.TryParse(choice.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var days) &&
        days is >= 1 and <= 35 ? days : 7;
}

public sealed class WindowsUpdateEntry
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Kb { get; init; } = "";
    public long Size { get; init; }
    public bool Downloaded { get; init; }
    public bool RequiresRestart { get; init; }
    public bool Optional { get; init; }
    public bool Feature { get; init; }
    public string TypeText => Feature ? "Windows version" : Optional ? "Optional" : "";
    public string SizeText => Size <= 0 ? "Unknown" : $"{Size / 1048576.0:0.0} MB";
    public string StateText => Downloaded ? "Downloaded" : "Waiting to download";
    public string RestartText => RequiresRestart ? "Restart" : "";

    public static WindowsUpdateEntry FromJson(JsonNode item) => new()
    {
        Id = item["id"]!.GetValue<string>(),
        Title = item["title"]!.GetValue<string>(),
        Kb = string.Join(", ", item["kb"]!.AsArray().Select(k => "KB" + k!.GetValue<string>())),
        Size = item["size"]!.GetValue<long>(),
        Downloaded = item["downloaded"]!.GetValue<bool>(),
        RequiresRestart = item["requiresRestart"]!.GetValue<bool>(),
        Optional = item["optional"]?.GetValue<bool>() == true,
        Feature = item["feature"]?.GetValue<bool>() == true,
    };
}

public sealed class WindowsUpdateWindow : Window
{
    ClientInfo _client;
    bool _working;
    readonly HostSettings _settings;
    readonly BulkOperationRunner? _run;
    readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    readonly TextBlock _status = Ui.StatusText();
    readonly TextBlock _pauseStatus = Ui.MutedText("Pause status: checking...", new Thickness(0, 0, 0, 8));
    readonly CheckBox _includeOptional = new() { Content = "Include optional/driver updates", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    List<WindowsUpdateEntry> _updates = new();

    public WindowsUpdateWindow(ClientInfo client, HostSettings settings, Window owner, BulkOperationRunner? run = null)
    {
        _client = client;
        _settings = settings;
        _run = run;
        Owner = owner;
        Title = $"Windows Update — {client.Name}";
        Width = 940;
        Height = 590;
        MinWidth = 680;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var view = new GridView();
        view.Columns.Add(Column("Update", nameof(WindowsUpdateEntry.Title), 360));
        view.Columns.Add(Column("KB", nameof(WindowsUpdateEntry.Kb), 120));
        view.Columns.Add(Column("Size", nameof(WindowsUpdateEntry.SizeText), 100));
        view.Columns.Add(Column("Download", nameof(WindowsUpdateEntry.StateText), 150));
        view.Columns.Add(Column("Restart", nameof(WindowsUpdateEntry.RestartText), 80));
        view.Columns.Add(Column("Type", nameof(WindowsUpdateEntry.TypeText), 120));
        _list.View = view;

        _includeOptional.ToolTip = "Include optional, preview and driver updates reported by Windows Update.";
        var scan = Button("_Check for updates", "Check for updates — query Windows Update now.", async (_, _) => await ScanAsync());
        var selected = Button("Install _selected", "Install selected — download and install the highlighted updates.", async (_, _) => await InstallAsync(false));
        var all = Button("Update _all", "Update all — download and install every update currently offered by Windows.", async (_, _) => await InstallAsync(true));
        var pause = Button("_Pause updates...", "Pause updates — pause Windows Update for a bounded number of days.", async (_, _) => await PauseAsync());
        var resume = Button("_Resume updates", "Resume updates — remove the Windows Update pause.", async (_, _) => await ResumeAsync());
        var version = Button("Windows _version...", "Windows version — see which Windows version this computer runs and make a newer one arrive as an ordinary update.", async (_, _) =>
        {
            if (!await WindowsVersionDialog.ShowAsync(this, [_client], _settings)) return;
            await RefreshPauseStatusAsync();
        });
        var restart = Button("_Restart for updates", "Restart for updates — schedule a 60-second delayed restart.", async (_, _) => await RestartAsync());
        var close = Button("_Close", "Close — return to the Host.", (_, _) => Close());
        close.IsCancel = true;
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in ComputerPicker.ForRow(new ComputerPicker(client, SwitchTo, () => _working
            ? Loc.F($"Updates are being installed on {_client.Name}. Wait until that has finished before showing another computer.") : null)))
            top.Children.Add(item);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in new[] { scan, selected, all, pause, resume, version, restart, close }) buttons.Children.Add(button);
        buttons.Children.Insert(1, _includeOptional);
        Ui.Spaced(buttons);
        var note = Ui.Explanation(
            "Updates are specific to this Windows installation. Select rows to install only those updates, or use Update all. Windows may require a restart; the restart command schedules a one-minute delay so the user can save work.",
            new Thickness(0, 0, 0, 8));
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_pauseStatus, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(note);
        root.Children.Add(_pauseStatus);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;
        Loaded += async (_, _) =>
        {
            await RefreshPauseStatusAsync();
            if (!ShowSaved()) await ScanAsync();
        };
        Ui.RefreshOnF5(this, ScanAsync);
        Ui.SelectAllOnCtrlA(this, _list);
    }

    static GridViewColumn Column(string header, string path, double width) => new()
    {
        Header = header,
        Width = width,
        DisplayMemberBinding = new Binding(path),
    };

    static Button Button(string content, string tooltip, RoutedEventHandler click)
    {
        var button = new Button { Content = content, ToolTip = tooltip, MinWidth = 110, Margin = new Thickness(0, 0, 8, 8) };
        button.Click += click;
        return button;
    }

    /// <summary>Opens with the list PC information or an earlier scan saved, and says how old it is.</summary>
    bool ShowSaved()
    {
        if (!WindowsUpdateSnapshotCache.TryGet(_client, out var saved) || saved.IncludedOptional != (_includeOptional.IsChecked == true)) return false;
        _updates = saved.Updates.Select(item => WindowsUpdateEntry.FromJson(item!)).ToList();
        _list.ItemsSource = _updates;
        _status.Text = WindowsUpdateSnapshotCache.SavedText(saved.At, DateTime.Now, _updates.Count);
        return true;
    }

    /// <summary>The computer was changed in the picker: show it, with an empty list until its data arrives.</summary>
    async void SwitchTo(ClientInfo computer)
    {
        _client = computer;
        Title = $"Windows Update — {computer.Name}";
        _updates = new();
        _list.ItemsSource = _updates;
        _pauseStatus.Text = "Pause status: checking...";
        _status.Text = ComputerPicker.NowShowing(computer);
        await RefreshPauseStatusAsync();
        if (!ShowSaved()) await ScanAsync();
    }

    async Task ScanAsync()
    {
        var computer = _client;
        try
        {
            _status.Text = "Checking Windows Update...";
            using var session = await ClientSession.OpenAsync(computer, _settings);
            var data = await session.CallAtAsync(Levels.System, Cmd.WindowsUpdateScan,
                new JsonObject { ["includeOptional"] = _includeOptional.IsChecked == true });
            if (computer != _client) return;   // another computer was chosen meanwhile
            var scanned = data["updates"]!.AsArray();
            WindowsUpdateSnapshotCache.Set(_client, _includeOptional.IsChecked == true, scanned);
            _updates = scanned.Select(item => WindowsUpdateEntry.FromJson(item!)).ToList();
            _list.ItemsSource = _updates;
            _status.Text = _updates.Count == 0 ? "Windows reports no available updates." : $"{_updates.Count} update(s) available on {_client.Name}.";
        }
        catch (Exception ex) { if (computer == _client) _status.Text = "Windows Update check failed: " + ex.Message; }
    }

    async Task InstallAsync(bool all)
    {
        var selected = _list.SelectedItems.Cast<WindowsUpdateEntry>().ToList();
        if (!all && selected.Count == 0)
        {
            MessageBox.Show(this, "Select at least one update, or choose Update all.", "Windows Update",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var countText = all ? "all currently offered updates" : $"{selected.Count} selected update(s)";
        if (MessageBox.Show(this, $"Download and install {countText} on {_client.Name}?",
                "Install Windows updates", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _working = true;
        try
        {
            _status.Text = "Downloading and installing Windows updates...";
            var args = new JsonObject { ["all"] = all };
            if (!all) args["ids"] = new JsonArray(selected.Select(update => (JsonNode)update.Id).ToArray());
            JsonObject data = null!;
            await OperateAsync(all ? "Windows Update: install all updates" : $"Windows Update: install {selected.Count} update(s)",
                async session => data = await session.CallAtAsync(Levels.System, Cmd.WindowsUpdateInstall, args));
            _status.Text = $"Installed {data["selected"]?.GetValue<int>() ?? 0} update(s)." +
                           (data["rebootRequired"]?.GetValue<bool>() == true ? " Restart required." : "");
            await ScanAsync();
        }
        catch (Exception ex) { _status.Text = "Windows Update install failed: " + ex.Message; }
        finally { _working = false; }
    }

    /// <summary>
    /// Runs one change on the shown computer. When the main window gave a runner it goes through the Operations center and the
    /// activity log (so it is recorded and can be repeated); a failure is passed on so this window shows it too.
    /// </summary>
    async Task OperateAsync(string title, Func<ClientSession, Task> action)
    {
        if (_run is null)
        {
            using var session = await ClientSession.OpenAsync(_client, _settings);
            await action(session);
            return;
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        await _run([_client], title, async (_, session) =>
        {
            try { await action(session); }
            catch (Exception ex) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); throw; }
        }, 1, "", (_, _) => { });
        failure?.Throw();
    }

    async Task PauseAsync()
    {
        var dlg = new FormDialog("Pause Windows Update", this)
            .Combo("period", "Pause period", WindowsUpdatePausePeriods.Choices, 1)
            .Note("Windows Update pause is a machine-wide policy and requires administrator rights.");
        if (!dlg.Ask("Pause")) return;
        var days = WindowsUpdatePausePeriods.ParseDays(dlg.S("period"));
        try
        {
            JsonObject data = null!;
            await OperateAsync($"Windows Update: pause for {days} day(s)",
                async session => data = await session.CallAtAsync(Levels.System, Cmd.WindowsUpdatePause, new JsonObject { ["days"] = days }));
            SetPauseStatus(data);
            _status.Text = $"Windows Update paused for {days} day(s).";
        }
        catch (Exception ex) { _status.Text = "Could not pause Windows Update: " + ex.Message; }
    }

    async Task ResumeAsync()
    {
        try
        {
            JsonObject data = null!;
            await OperateAsync("Windows Update: resume updates",
                async session => data = await session.CallAtAsync(Levels.System, Cmd.WindowsUpdateResume));
            SetPauseStatus(data);
            _status.Text = "Windows Update pause removed.";
        }
        catch (Exception ex) { _status.Text = "Could not resume Windows Update: " + ex.Message; }
    }

    async Task RefreshPauseStatusAsync()
    {
        if (!_client.SupportsCommand(Cmd.WindowsUpdateStatus))
        {
            _pauseStatus.Text = $"Pause status: unavailable until the Client supports protocol {Protocol.RequiredVersion(Cmd.WindowsUpdateStatus)}.";
            return;
        }
        try
        {
            using var session = await ClientSession.OpenAsync(_client, _settings);
            SetPauseStatus(await session.CallAtAsync(Levels.System, Cmd.WindowsUpdateStatus));
        }
        catch (Exception ex) { _pauseStatus.Text = "Pause status: unavailable - " + ex.Message; }
    }

    void SetPauseStatus(JsonObject data)
    {
        var paused = data["paused"]?.GetValue<bool>() == true;
        var until = data["untilUtc"]?.GetValue<string>() ?? "";
        var pause = paused && DateTimeOffset.TryParse(until, out var expiry)
            ? $"Pause status: paused until {expiry.ToLocalTime():g}."
            : paused ? "Pause status: paused." : "Pause status: running normally.";
        _pauseStatus.Text = $"{WindowsVersionDialog.Line(data)}. {pause}";
    }

    async Task RestartAsync()
    {
        if (MessageBox.Show(this, $"Schedule {_client.Name} to restart in 60 seconds to finish Windows Update?",
                "Restart for Windows Update", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            await OperateAsync("Windows Update: restart to finish updates",
                session => session.CallAtAsync(Levels.System, Cmd.WindowsUpdateRestart));
            _status.Text = "Restart scheduled in 60 seconds.";
        }
        catch (Exception ex) { _status.Text = "Could not schedule restart: " + ex.Message; }
    }
}
