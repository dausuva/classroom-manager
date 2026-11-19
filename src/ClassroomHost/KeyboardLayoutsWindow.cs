// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// The signed-in users' keyboard layouts on the selected computers, side by side: add or remove one,
/// choose the default, and keep a room default to bring every computer to.
/// </summary>
public sealed class KeyboardLayoutsWindow : Window
{
    readonly IReadOnlyList<ClientInfo> _clients;
    readonly HostSettings _settings;
    readonly BulkOperationRunner? _run;
    readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
        CanUserAddRows = false, CanUserDeleteRows = false,
    };
    readonly TextBlock _status = Ui.StatusText();
    Dictionary<ClientInfo, ComputerKeyboard> _keyboards = new();

    public KeyboardLayoutsWindow(IReadOnlyList<ClientInfo> clients, HostSettings settings, Window owner, BulkOperationRunner? run = null)
    {
        _clients = clients;
        _settings = settings;
        _run = run;
        Owner = owner;
        Title = clients.Count == 1 ? $"Keyboard layouts - {clients[0].Name}" : $"Keyboard layouts - {clients.Count} computers";
        Width = 1040;
        Height = 560;
        MinWidth = 760;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var (header, path, width) in new[]
                 {
                     ("Layout", nameof(ComparedKeyboardLayout.Name), 260.0), ("Language", nameof(ComparedKeyboardLayout.Language), 80.0),
                     ("Installed", nameof(ComparedKeyboardLayout.Installed), 80.0), ("Missing from", nameof(ComparedKeyboardLayout.MissingFrom), 200.0),
                     ("Default on", nameof(ComparedKeyboardLayout.DefaultOn), 170.0), ("Room default", nameof(ComparedKeyboardLayout.InRoomDefault), 100.0),
                     ("Windows name", nameof(ComparedKeyboardLayout.Tip), 120.0),
                 })
            _grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width });

        var refresh = new Button { Content = "_Recheck", Tag = FluentIcons.Refresh, ToolTip = "Recheck (F5) — read the keyboard layouts of every selected computer again." };
        refresh.Click += async (_, _) => await LoadAsync();
        var add = new Button { Content = "_Add layout...", Tag = FluentIcons.Add, ToolTip = "Add layout — add a keyboard layout for the signed-in users of the selected computers." };
        add.Click += async (_, _) => await AddAsync();
        var remove = new Button { Content = "Re_move selected", Tag = FluentIcons.Delete, ToolTip = "Remove selected — take the selected layout away on the computers that have it. The last layout of a computer is kept." };
        remove.Click += async (_, _) => await ChangeAsync(remove: true);
        var makeDefault = new Button { Content = "Make _default", Tag = FluentIcons.Accept, ToolTip = "Make default — make the selected layout the one students get first, adding it where it is missing." };
        makeDefault.Click += async (_, _) => await ChangeAsync(remove: false);
        var saveRoom = new Button { Content = "_Save as room default...", Tag = FluentIcons.Save, ToolTip = "Save as room default — remember a set of layouts and a default to bring computers to later." };
        saveRoom.Click += (_, _) => SaveRoomDefault();
        var applyRoom = new Button { Content = "A_pply room default...", Tag = FluentIcons.Play, ToolTip = "Apply room default — add the room's layouts where they are missing and set its default; optionally remove the others." };
        applyRoom.Click += async (_, _) => await ApplyRoomDefaultAsync();
        var close = new Button { Content = "_Close", Tag = FluentIcons.Close, IsCancel = true };
        close.Click += (_, _) => Close();

        var buttons = Ui.Spaced(new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { refresh, add, remove, makeDefault, saveRoom, applyRoom, close } });
        var note = Ui.Explanation(
            "Layouts belong to the signed-in user, as in Windows Settings > Time & language > Language & region; a computer nobody is signed in to cannot be read. " +
            "Changes need an Admin key.",
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
        Loaded += async (_, _) => await LoadAsync();
        Ui.RefreshOnF5(this, LoadAsync);
    }

    async Task LoadAsync()
    {
        _status.Text = "Reading keyboard layouts...";
        var read = new Dictionary<ClientInfo, ComputerKeyboard>();
        var errors = new List<string>();
        await Task.WhenAll(_clients.Select(async client =>
        {
            try
            {
                using var session = await ClientSession.OpenAsync(client, _settings);
                var keyboard = ComputerKeyboard.FromJson(await session.CallAtAsync(Levels.User, Cmd.KeyboardList));
                lock (read) read[client] = keyboard;
            }
            catch (Exception ex) { lock (errors) errors.Add($"{client.Name}: {ex.Message}"); }
        }));
        _keyboards = read;
        _grid.ItemsSource = KeyboardLayoutComparison.Rows(_keyboards, _settings.KeyboardRoomDefault);
        _status.Text = $"{read.Count} of {_clients.Count} computer(s) read." + (errors.Count > 0 ? " Not read: " + string.Join("; ", errors) : "");
    }

    async Task RunAsync(List<ClientInfo> targets, string title, Func<ClientInfo, ClientSession, Task> action)
    {
        if (targets.Count == 0) return;
        if (_run is not null) await _run(targets, title, action, 4, "", (_, _) => { });
        else
            foreach (var client in targets)
            {
                try
                {
                    using var session = await ClientSession.OpenAsync(client, _settings);
                    await action(client, session);
                }
                catch (Exception ex) { _status.Text = $"{client.Name}: {ex.Message}"; }
            }
        await LoadAsync();
    }

    static Task Set(ClientSession session, string key, string tip) =>
        session.CallAtAsync(Levels.User, Cmd.KeyboardSet, new JsonObject { [key] = tip });

    async Task AddAsync()
    {
        var choices = KeyboardLayouts.Common.Select(entry => $"{entry.Name} ({entry.Tip})").ToArray();
        var dlg = new FormDialog("Add keyboard layout", this)
            .Combo("common", "Layout", choices)
            .Text("tip", "Or another layout's Windows name (e.g. 0427:00010427)")
            .Check("default", "Make it the default layout", false);
        if (!dlg.Ask("Add")) return;
        var typed = dlg.S("tip");
        var tip = typed.Length > 0 ? typed : KeyboardLayouts.Common[Array.IndexOf(choices, dlg.S("common"))].Tip;
        if (!KeyboardLayouts.IsTip(tip))
        {
            MessageBox.Show(this, $"'{tip}' is not a keyboard layout name such as 0427:00010427.", "Add keyboard layout", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        tip = KeyboardLayouts.Normalize(tip);
        var key = dlg.B("default") ? KeyboardLayouts.MakeDefault : KeyboardLayouts.Add;
        await RunAsync([.. _keyboards.Keys], $"Keyboard layout: add {tip}", (_, session) => Set(session, key, tip));
    }

    async Task ChangeAsync(bool remove)
    {
        if (_grid.SelectedItem is not ComparedKeyboardLayout row)
        {
            MessageBox.Show(this, "Select a layout first.", "Keyboard layouts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (remove)
        {
            if (MessageBox.Show(this, $"Remove {row.Name} from {row.Computers.Count} computer(s)?", "Keyboard layouts",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await RunAsync([.. row.Computers], $"Keyboard layout: remove {row.Tip}", (_, session) => Set(session, KeyboardLayouts.Remove, row.Tip));
        }
        else
            await RunAsync([.. _keyboards.Keys], $"Keyboard layout: default {row.Tip}", (_, session) => Set(session, KeyboardLayouts.MakeDefault, row.Tip));
    }

    void SaveRoomDefault()
    {
        var rows = (_grid.ItemsSource as IEnumerable<ComparedKeyboardLayout>)?.ToList() ?? [];
        if (rows.Count == 0) return;
        var labels = rows.Select(row => $"{row.Name} ({row.Tip})").ToList();
        var current = _settings.KeyboardRoomDefault;
        var dlg = new FormDialog("Save as room default", this)
            .MultiSelect("layouts", "Layouts every computer should have", labels,
                rows.Where(row => current?.Layouts.Contains(row.Tip) ?? row.Computers.Count == _keyboards.Count).Select(row => $"{row.Name} ({row.Tip})"))
            .Combo("default", "Default layout", labels.ToArray(), Math.Max(0, rows.FindIndex(row => row.Tip == current?.Default)));
        if (!dlg.Ask("Save")) return;
        var chosen = dlg.S("layouts").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var room = new KeyboardRoomDefault
        {
            Layouts = rows.Where(row => chosen.Contains($"{row.Name} ({row.Tip})")).Select(row => row.Tip).ToList(),
            Default = rows[labels.IndexOf(dlg.S("default"))].Tip,
        };
        if (!room.Layouts.Contains(room.Default)) room.Layouts.Insert(0, room.Default);
        _settings.KeyboardRoomDefault = room;
        _settings.Save();
        _grid.ItemsSource = KeyboardLayoutComparison.Rows(_keyboards, room);
        _status.Text = $"Room default saved: {room.Layouts.Count} layout(s), default {room.Default}.";
    }

    async Task ApplyRoomDefaultAsync()
    {
        if (_settings.KeyboardRoomDefault is not { Layouts.Count: > 0 } room)
        {
            MessageBox.Show(this, "Save a room default first.", "Keyboard layouts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new FormDialog("Apply room default", this)
            .Note($"Adds {string.Join(", ", room.Layouts)} where missing and makes {room.Default} the default.")
            .Check("removeOthers", "Also remove the layouts that are not in the room default", false);
        if (!dlg.Ask("Apply")) return;
        var removeOthers = dlg.B("removeOthers");
        var plans = _keyboards.ToDictionary(pair => pair.Key, pair => KeyboardLayoutComparison.Plan(pair.Value, room, removeOthers));
        var targets = plans.Where(pair => pair.Value.Add.Count + pair.Value.Remove.Count > 0 || pair.Value.Default.Length > 0).Select(pair => pair.Key).ToList();
        if (targets.Count == 0)
        {
            _status.Text = "Every computer read already matches the room default.";
            return;
        }
        await RunAsync(targets, "Keyboard layouts: room default", async (client, session) =>
        {
            var (toAdd, toRemove, makeDefault) = plans[client];
            foreach (var tip in toAdd.Where(tip => tip != makeDefault)) await Set(session, KeyboardLayouts.Add, tip);
            if (makeDefault.Length > 0) await Set(session, KeyboardLayouts.MakeDefault, makeDefault);
            foreach (var tip in toRemove) await Set(session, KeyboardLayouts.Remove, tip);
        });
    }
}
