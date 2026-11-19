// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClassroomManager.Appearance;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// The one Appearance window: every setting the selected computers report (<c>appearance.catalog</c>), drawn from what they say about it,
/// and applied or reset through the Appearance module's ports. A setting left at "no change" is not sent. Nothing here knows a single
/// Windows setting by name; a new setting appears when a Client reports it.
/// </summary>
public sealed class AppearanceSettingsWindow : Window
{
    readonly IReadOnlyList<ClientInfo> _clients;
    readonly HostSettings _settings;
    readonly StackPanel _list = new();
    readonly TextBlock _status = Ui.StatusText();
    readonly ListBox _results = new() { MinHeight = 90, MaxHeight = 160 };
    readonly List<Editor> _editors = [];
    IReadOnlyList<AppearanceRow> _rows = [];
    bool _busy;

    sealed record Editor(AppearanceRow Row, FrameworkElement Control, Func<string?> Chosen, Action<string?> Choose);

    public AppearanceSettingsWindow(IReadOnlyList<ClientInfo> clients, HostSettings settings, Window owner)
    {
        _clients = clients;
        _settings = settings;
        Owner = owner;
        Title = Loc.F($"Appearance - {clients.Count} computer(s)");
        Width = 900;
        Height = 700;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var apply = Button("_Apply to selected computers", "▶", async () => await ApplyAsync());
        var reset = Button("_Reset what was changed", "↩", async () => await ResetAsync());
        var save = Button("_Save as preset...", "📤", () => SavePreset());
        var load = Button("_Load preset...", "📥", () => LoadPreset());
        var delete = Button("_Delete preset...", "🗑", () => DeletePreset());
        var export = Button("E_xport preset...", "⬆", () => ExportPreset());
        var import = Button("_Import preset...", "⬇", () => ImportPreset());
        var refresh = Button("Re_fresh", "↻", async () => await LoadAsync());
        var close = new Button { Content = "_Close", Tag = "✕", MinWidth = 120 };
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in new[] { apply, reset, save, load, delete, export, import, refresh, close }) buttons.Children.Add(button);
        Ui.Spaced(buttons);
        ModuleUiPolicy.WatchVisibility(this, settings, BuiltInModules.Appearance, apply, reset, save, load, delete, export, import, refresh);

        var note = Ui.Explanation("Each line is a Windows setting the selected computers report. Leave a line at (no change) to keep it as it is. " +
            "Reset puts a setting back to exactly what it was before Classroom Manager first changed it.");
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list, Margin = new Thickness(0, 8, 0, 0) };
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(_results, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(_results);
        root.Children.Add(buttons);
        root.Children.Add(scroll);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
    }

    static Button Button(string text, string icon, Func<Task> action) => Button(text, icon, (Action)(() => _ = action()));

    static Button Button(string text, string icon, Action action)
    {
        var button = new Button { Content = text, Tag = icon, MinWidth = 145, ToolTip = text.Replace("_", "") };
        button.Click += (_, _) => action();
        return button;
    }

    async Task LoadAsync()
    {
        if (_busy || !BuiltInModules.Enabled(_settings, BuiltInModules.Appearance)) return;
        _busy = true;
        try
        {
            _status.Text = Loc.T("Reading the appearance settings...");
            var errors = new List<string>();
            var answers = await Task.WhenAll(_clients.Select(async client =>
            {
                if (!client.SupportsCommand(Cmd.AppearanceCatalog))
                {
                    errors.Add(Loc.F($"{client.Name}: needs Client {AppearanceWire.RequiredVersion} or newer for these settings."));
                    return null;
                }
                try
                {
                    using var session = await ClientSession.OpenAsync(client, _settings);
                    var (appearance, machine) = ModuleRuns.OpenAppearance(session, client);
                    return await appearance.CatalogAsync(machine, CancellationToken.None);
                }
                catch (Exception ex) { lock (errors) errors.Add($"{client.Name}: {ex.Message}"); return null; }
            }));
            _rows = AppearanceMerge.Rows(answers);
            Rebuild();
            var unreachable = answers.Count(answer => answer is null);
            _status.Text = _rows.Count == 0 ? Loc.T("No appearance settings were reported.")
                : Loc.F($"{_rows.Count} setting(s) from {answers.Length - unreachable} of {answers.Length} computer(s).") + (errors.Count == 0 ? "" : " " + string.Join(" ", errors));
        }
        finally { _busy = false; }
    }

    void Rebuild()
    {
        _list.Children.Clear();
        _editors.Clear();
        string? category = null;
        foreach (var row in _rows)
        {
            if (row.Setting.Category != category)
            {
                category = row.Setting.Category;
                _list.Children.Add(new TextBlock { Text = AppearanceSettingsPresentation.Text(category), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            }
            var editor = Editor_(row);
            _editors.Add(editor);
            var line = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            var label = new TextBlock { Text = AppearanceSettingsPresentation.Text(row.Setting.Label), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var info = Ui.MutedText(AppearanceSettingsPresentation.Describe(row), new Thickness(12, 0, 0, 0));
            UiTranslator.SetPreserveText(label, true);
            UiTranslator.SetPreserveText(info, true);
            Grid.SetColumn(editor.Control, 1);
            Grid.SetColumn(info, 2);
            line.Children.Add(label);
            line.Children.Add(editor.Control);
            line.Children.Add(info);
            _list.Children.Add(line);
        }
    }

    static Editor Editor_(AppearanceRow row)
    {
        var setting = row.Setting;
        if (setting.Kind is AppearanceWireReader.KindToggle or AppearanceWireReader.KindChoice)
        {
            var values = setting.Kind == AppearanceWireReader.KindToggle
                ? new List<(string Value, string Label)> { ("true", "On"), ("false", "Off") }
                : setting.Choices.Select(choice => (choice.Value, choice.Label)).ToList();
            var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 0) };
            combo.Items.Add(AppearanceSettingsPresentation.NoChange);
            foreach (var (_, label) in values) combo.Items.Add(AppearanceSettingsPresentation.Text(label));
            combo.SelectedIndex = 0;
            UiTranslator.SetPreserveText(combo, true);
            return new(row, combo, () => combo.SelectedIndex > 0 ? values[combo.SelectedIndex - 1].Value : null,
                value => combo.SelectedIndex = Math.Max(0, values.FindIndex(item => item.Value == value) + 1));
        }
        var box = new TextBox { ToolTip = AppearanceSettingsPresentation.NoChange };
        return new(row, box, () => box.Text.Trim().Length == 0 ? null : box.Text.Trim(), value => box.Text = value ?? "");
    }

    Dictionary<string, string> Chosen() => _editors.Select(editor => (editor.Row.Setting.Id, Value: editor.Chosen()))
        .Where(item => item.Value is not null).ToDictionary(item => item.Id, item => item.Value!, StringComparer.Ordinal);

    async Task ApplyAsync()
    {
        var values = Chosen();
        if (_busy || values.Count == 0) { _status.Text = Loc.T("Nothing is chosen to change."); return; }
        var preset = new AppearancePreset("(now)", values);
        var problems = AppearancePresets.Plan(preset, _rows);
        if (problems.Count > 0) { MessageBox.Show(this, string.Join("\n", problems.Select(Loc.T)), Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var risky = _rows.Where(row => values.ContainsKey(row.Setting.Id) && row.Setting.Risk == AppearanceWire.RiskConfirm).ToList();
        if (risky.Count > 0 && MessageBox.Show(this, Loc.F($"{string.Join(", ", risky.Select(row => AppearanceSettingsPresentation.Text(row.Setting.Label)))} can leave a student without a usable screen. The computer asks the person there to keep the change and goes back by itself when nobody answers. Continue?"),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (MessageBox.Show(this, Loc.F($"Apply {values.Count} setting(s) to {_clients.Count} computer(s)?"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunOnComputersAsync(Loc.T("Applying..."), (appearance, machine) => appearance.SetAsync(machine, values, CancellationToken.None));
    }

    async Task ResetAsync()
    {
        var ids = _rows.Where(row => row.ChangedOn > 0).Select(row => row.Setting.Id).ToList();
        if (_busy || ids.Count == 0) { _status.Text = Loc.T("Classroom Manager has not changed any of these settings."); return; }
        if (MessageBox.Show(this, Loc.F($"Put {ids.Count} setting(s) back to what they were before Classroom Manager changed them, on {_clients.Count} computer(s)?"),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunOnComputersAsync(Loc.T("Resetting..."), (appearance, machine) => appearance.ResetAsync(machine, ids, CancellationToken.None));
    }

    async Task RunOnComputersAsync(string working, Func<AppearanceOperations, ClassroomManager.Modules.ModuleComputer, Task<IReadOnlyList<AppearanceResult>>> action)
    {
        _busy = true;
        try
        {
            _status.Text = working;
            _results.Items.Clear();
            await Task.WhenAll(_clients.Select(async client =>
            {
                string line;
                try
                {
                    using var session = await ClientSession.OpenAsync(client, _settings);
                    var (appearance, machine) = ModuleRuns.OpenAppearance(session, client);
                    line = AppearanceSettingsPresentation.Summary(client.Name, await action(appearance, machine));
                }
                catch (Exception ex) { line = $"{client.Name}: {ex.Message}"; }
                await Dispatcher.InvokeAsync(() => _results.Items.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap }));
            }));
        }
        finally { _busy = false; }
        await LoadAsync();
    }

    void SavePreset()
    {
        var values = Chosen();
        if (values.Count == 0) { _status.Text = Loc.T("Nothing is chosen to save."); return; }
        var dialog = new FormDialog(Loc.T("Save as preset"), this).Text("name", "Preset name", "");
        if (!dialog.Ask("Save")) return;
        var preset = new AppearancePreset(dialog.S("name"), values);
        var problems = AppearancePresets.Problems(preset);
        if (problems.Count > 0) { MessageBox.Show(this, string.Join("\n", problems.Select(Loc.T)), Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _settings.AppearancePresets.RemoveAll(existing => existing.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
        _settings.AppearancePresets.Add(new AppearancePresetRecord { Name = preset.Name, Values = new Dictionary<string, string>(values, StringComparer.Ordinal) });
        _settings.Save();
        _status.Text = Loc.F($"Saved the preset \"{preset.Name}\" ({values.Count} setting(s)).");
    }

    void LoadPreset()
    {
        if (_settings.AppearancePresets.Count == 0) { _status.Text = Loc.T("There are no saved presets yet."); return; }
        var names = _settings.AppearancePresets.Select(preset => preset.Name).ToArray();
        var dialog = new FormDialog(Loc.T("Load preset"), this).Combo("preset", "Preset", names);
        if (!dialog.Ask("Load")) return;
        var record = _settings.AppearancePresets.First(preset => preset.Name == dialog.S("preset"));
        var preset = new AppearancePreset(record.Name, record.Values);
        var problems = AppearancePresets.Plan(preset, _rows);
        if (problems.Count > 0) { MessageBox.Show(this, string.Join("\n", problems.Select(Loc.T)), Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        foreach (var editor in _editors) editor.Choose(preset.Values.GetValueOrDefault(editor.Row.Setting.Id));
        _status.Text = Loc.F($"Loaded the preset \"{preset.Name}\". Check the lines, then apply.");
    }

    void ExportPreset()
    {
        if (_settings.AppearancePresets.Count == 0) { _status.Text = Loc.T("There are no saved presets yet."); return; }
        var names = _settings.AppearancePresets.Select(preset => preset.Name).ToArray();
        var dialog = new FormDialog(Loc.T("Export preset"), this).Combo("preset", "Preset", names);
        if (!dialog.Ask("Export")) return;
        var record = _settings.AppearancePresets.First(preset => preset.Name == dialog.S("preset"));
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Export preset"), FileName = record.Name + AppearancePresetFiles.Extension, DefaultExt = ".json", Filter = Loc.T("Appearance preset") + "|*.json",
        };
        if (picker.ShowDialog(this) != true) return;
        try { AppearancePresetFiles.Write(new AppearancePreset(record.Name, record.Values), picker.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { _status.Text = Loc.F($"The preset could not be saved: {ex.Message}"); return; }
        _status.Text = Loc.F($"Exported the preset \"{record.Name}\" to {picker.FileName}.");
    }

    void ImportPreset()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Import preset"), Filter = Loc.T("Appearance preset") + "|*.json" };
        if (picker.ShowDialog(this) != true) return;
        var (preset, problems) = AppearancePresetFiles.Read(picker.FileName);
        if (preset is null) { MessageBox.Show(this, string.Join(Environment.NewLine, problems.Select(Loc.T)), Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var replaces = _settings.AppearancePresets.Any(existing => existing.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
        if (replaces && MessageBox.Show(this, Loc.F($"A preset named \"{preset.Name}\" is already saved. Replace it?"), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _settings.AppearancePresets.RemoveAll(existing => existing.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
        _settings.AppearancePresets.Add(new AppearancePresetRecord { Name = preset.Name, Values = new Dictionary<string, string>(preset.Values, StringComparer.Ordinal) });
        _settings.Save();
        _status.Text = Loc.F($"Imported the preset \"{preset.Name}\" ({preset.Values.Count} setting(s)). Use Load preset to see it.");
    }

    void DeletePreset()
    {
        if (_settings.AppearancePresets.Count == 0) { _status.Text = Loc.T("There are no saved presets yet."); return; }
        var names = _settings.AppearancePresets.Select(preset => preset.Name).ToArray();
        var dialog = new FormDialog(Loc.T("Delete preset"), this).Combo("preset", "Preset", names);
        if (!dialog.Ask("Delete")) return;
        _settings.AppearancePresets.RemoveAll(preset => preset.Name == dialog.S("preset"));
        _settings.Save();
        _status.Text = Loc.F($"Deleted the preset \"{dialog.S("preset")}\".");
    }
}

/// <summary>The words of the Appearance window: the labels the Clients report are English, and are shown in the language of the Host.</summary>
public static class AppearanceSettingsPresentation
{
    public static string NoChange => Loc.T("(no change)");

    /// <summary>A label a Client reports, in the Host's language; one it does not know stays as the Client said it.</summary>
    public static string Text(string english) => Loc.T(english);

    // Listed here so the translation file carries them; the Clients report the same English words.
    static readonly string[] Known =
    {
        "Colours", "Desktop", "On", "Off", "Light", "Dark", "Fill", "Fit", "Stretch", "Tile", "Centre", "Span",
        "Default app mode", "Default Windows mode", "Transparency effects",
        "Show accent colour on Start, taskbar and action centre", "Show accent colour on title bars and window borders",
        "Show desktop icons", "This PC on the desktop", "User's files on the desktop", "Network on the desktop",
        "Recycle Bin on the desktop", "Control Panel on the desktop", "Wallpaper fit",
    };

    /// <summary>What the computers show now, and how many have the setting.</summary>
    public static string Describe(AppearanceRow row)
    {
        var shown = row.CurrentValues.Select(value => Label(row.Setting, value)).ToArray();
        var now = row.Mixed ? Loc.F($"Now: different ({string.Join(", ", shown)})") : Loc.F($"Now: {shown.FirstOrDefault() ?? ""}");
        var parts = new List<string> { now };
        if (row.SupportedBy < row.Total) parts.Add(Loc.F($"on {row.SupportedBy} of {row.Total} computer(s)"));
        if (row.ChangedOn > 0) parts.Add(Loc.F($"changed by Classroom Manager on {row.ChangedOn}"));
        if (row.Setting.Apply == AppearanceWire.ApplySignIn) parts.Add(Loc.T("shows at the next sign-in"));
        return string.Join("; ", parts);
    }

    static string Label(AppearanceSettingInfo setting, string value) => setting.Kind switch
    {
        AppearanceWireReader.KindToggle => Loc.T(value == "true" ? "On" : "Off"),
        AppearanceWireReader.KindChoice => Text(setting.Choices.FirstOrDefault(choice => choice.Value == value)?.Label ?? value),
        _ => value,
    };

    /// <summary>One line for one computer: how many settings were applied, left as they were, not supported, or failed.</summary>
    public static string Summary(string computer, IReadOnlyList<AppearanceResult> results)
    {
        int Count(string status) => results.Count(result => result.Status == status);
        var parts = new List<string>();
        if (Count(AppearanceWire.Applied) > 0) parts.Add(Loc.F($"{Count(AppearanceWire.Applied)} applied"));
        if (Count(AppearanceWire.Restored) > 0) parts.Add(Loc.F($"{Count(AppearanceWire.Restored)} restored"));
        if (Count(AppearanceWire.Unchanged) > 0) parts.Add(Loc.F($"{Count(AppearanceWire.Unchanged)} already that way"));
        if (Count(AppearanceWire.Unsupported) > 0) parts.Add(Loc.F($"{Count(AppearanceWire.Unsupported)} not supported on this Windows"));
        if (results.FirstOrDefault(result => result.ConfirmSeconds > 0) is { } waiting)
            parts.Add(Loc.F($"waiting for the person at the computer to keep the change ({waiting.ConfirmSeconds} seconds)"));
        foreach (var failed in results.Where(result => result.Status == AppearanceWire.Failed)) parts.Add($"{failed.Id}: {failed.Reason}");
        return $"{computer}: " + (parts.Count == 0 ? Loc.T("nothing to report") : string.Join("; ", parts));
    }
}
