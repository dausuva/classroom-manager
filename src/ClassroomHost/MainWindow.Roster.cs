// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ClassroomManager.Shared;
#if BROWSER_INVENTORY_MODULE
using ClassroomManager.BrowserInventory;
#endif
#if REPORTING_MODULE
using ClassroomManager.Reporting;
#endif

namespace ClassroomManager.Host;

public partial class MainWindow
{
    ClientInfo AddClient(string address, string name, int port = 0) => _roster.Add(address, name, port);

    ClientInfo AddDiscoveredClient(Discovery.Found found) => _roster.Merge(found);

    void SaveComputers() => _rosterPersistence.Save(_clients);

    bool ClientInSelectedNetwork(ClientInfo client) => RosterRules.InNetwork(client.Address, _networkScope);

    bool ClientMatchesFilters(ClientInfo client) => ClientInSelectedNetwork(client) && RosterRules.InRoom(client.Room, _room);

    List<string> Rooms() => RosterRules.Rooms(_clients.Where(ClientInSelectedNetwork).Select(c => c.Room));

    void RefreshRooms()
    {
        var (items, index, room) = RosterRules.RoomChoices(Rooms(), _room);
        _room = room;
        RoomBox.ItemsSource = items;
        RoomBox.SelectedIndex = index;
    }

    void RoomBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _room = RoomBox.SelectedIndex <= 0 ? "" : (RoomBox.SelectedItem as string ?? "");
        _view.Refresh();
        UpdateCollectionState();
    }

    void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The Help actions live in Settings, but feedback opened there should still describe the
        // workflow the user came from. Ctrl+Shift+F remains available for Settings-specific feedback.
        if (e.Source != MainTabs || MainTabs.SelectedItem == SettingsTab) return;
        _lastFeedbackContext = FeedbackUi.Capture(this);
    }

    void CaptureFeedback_Click(object sender, RoutedEventArgs e) =>
        FeedbackCaptureWindow.Open(this, _lastFeedbackContext ?? FeedbackUi.Capture(this));

    void ClientGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateCollectionState();
        }

    void SelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(UpdateCollectionState, DispatcherPriority.DataBind);
    }

    void ClientGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SelectAllVisible();
            return;
        }
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None ||
            FindVisualParent<CheckBox>(e.OriginalSource as DependencyObject) is not null) return;

        var rows = ClientGrid.SelectedItems.Cast<ClientInfo>().ToList();
        if (rows.Count == 0 && ClientGrid.CurrentItem is ClientInfo current) rows.Add(current);
        if (rows.Count == 0) return;

        e.Handled = true;
        var select = !rows.All(client => client.IsSelected);
        foreach (var client in rows) client.IsSelected = select;
        UpdateCollectionState();
    }

    void ClientGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindVisualParent<System.Windows.Controls.DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is ClientInfo client)
            new ClientInfoWindow(client, _settings, this,
                label => ToolbarAction(label) is { Visibility: Visibility.Visible },
                label => RunActionFor(client, label)).Show();
    }

    /// <summary>The toolbar action with this English label, wherever the teacher arranged it.</summary>
    UIElement? ToolbarAction(string label) => _toolbarTabs.SelectMany(tab => tab.Actions)
        .FirstOrDefault(action => ToolbarArrangement.Label(action.Key).Equals(label, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Set while an action runs for one computer from Computer information; the ticked computers stay ticked.</summary>
    List<ClientInfo>? _onlyFor;

    /// <summary>Presses a toolbar action for one computer, as if it were the only one selected.</summary>
    void RunActionFor(ClientInfo client, string label)
    {
        if (ToolbarAction(label) is not { } action) return;
        // Every action reads the selection before its first await, so the override covers exactly that read.
        _onlyFor = [client];
        try { action.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
        finally { _onlyFor = null; }
    }

    static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject => Ui.FindParent<T>(child);

    async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            CommandSearchBox.Focus();
            CommandSearchBox.SelectAll();
            UpdateCommandSearch();
            return;
        }
        if (e.Key == Key.Escape && (CommandSearchPopup.IsOpen || CommandSearchBox.IsKeyboardFocusWithin))
        {
            CommandSearchPopup.IsOpen = false;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await CheckAllAsync();
        }
        else if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Help_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.T && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            // Reaches Arrange toolbar from any tab, whatever the toolbar shows.
            e.Handled = true;
            ArrangeToolbar_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape && SelectedRaw().Count > 0)
        {
            e.Handled = true;
            ClearSelection();
        }
    }

    /// <summary>
    /// The Status column takes the room that is left, and a table with such a column makes room in a
    /// narrow window by squeezing the others down to a sliver ("Sv", "P", "K" for three whole titles,
    /// longer still in Lithuanian). Each column that fits its content is therefore told never to be
    /// narrower than that content; the table then scrolls sideways instead.
    /// </summary>
    void KeepColumnsReadable()
    {
        foreach (var column in ClientGrid.Columns)
        {
            if (!column.Width.IsAuto) continue;
            var needed = column.Width.DesiredValue;
            if (double.IsNaN(needed) || needed <= 0 || needed > 600) continue;
            if (needed > column.MinWidth + 0.5) column.MinWidth = Math.Ceiling(needed);
        }
    }

    void UpdateCollectionState()
    {
        if (!IsInitialized) return;
        var snapshot = HostSelectionSnapshot.Capture(_view.Cast<ClientInfo>(), _clients.Count,
            _clients.Any(ClientInSelectedNetwork), _onlyFor);
        var target = OperationTargetSummary.Format(snapshot.Targets);
        SelectionText.Text = target.Heading;
        SelectionDetailText.Text = target.Detail;
        UpdateCommandAvailability(snapshot.Targets.Count);
        if (CommandSearchPopup.IsOpen) UpdateCommandSearch();
        EmptyStatePanel.Visibility = snapshot.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateText.Text = snapshot.EmptyReason switch
        {
            HostEmptyStateReason.NoSavedComputers => "Scan the network or add a computer to get started. Discovered computers are monitored after you assign them to a room.",
            HostEmptyStateReason.NoNetworkMatch => "No saved IP-addressed computers match the selected network. Change the discovery subnet in Host settings, scan this network, or add a computer by name.",
            _ => $"No computers are assigned to '{_room}'. Choose (all rooms) or assign computers to this room.",
        };
    }

    /// <summary>Gives the selected computer a label of the teacher's own; the computer keeps its Windows name.</summary>
    void SetDisplayName_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count != 1)
        {
            MessageBox.Show(this, "Select one computer to give it a display name.", "Display name");
            return;
        }
        var computer = targets[0];
        var dlg = new FormDialog("Display name", this)
            .Target(targets)
            .Note(Loc.F($"Only this Host shows the name. The computer is still called {computer.ComputerName} in Windows and on the network. Leave empty to go back to its Windows name."))
            .Text("alias", "Display name", computer.Alias);
        if (!dlg.Ask("Set")) return;

        computer.Alias = dlg.S("alias");
        SaveComputers();
        _view.Refresh();
    }

    /// <summary>
    /// Changes the Windows computer name of one computer. Windows applies it after a restart, which is offered;
    /// the Host updates its record by itself when the computer answers again under the new name.
    /// </summary>
    async void RenameComputer_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count != 1)
        {
            MessageBox.Show(this, "Select exactly one computer: each computer needs a name of its own.", "Rename computer");
            return;
        }
        if (!EnsureFeatureSupported(targets, Cmd.SystemRename, "Rename computer")) return;
        var computer = targets[0];
        var taken = _clients.Where(other => other != computer).Select(other => other.ComputerName).ToList();
        var dlg = new FormDialog("Rename computer", this)
            .Target(targets)
            .Note(Loc.F($"Changes the Windows name of {computer.ComputerName}, the name it has on the network and in Windows. It takes effect after a restart; until then the old name stays. A computer joined to a domain is refused: its name is its account in the domain.") +
                  "\n\n" + Loc.T("To only change what this Host calls the computer, use Display name instead."))
            .Text("name", "New computer name (letters, digits and hyphens, at most 15)", computer.ComputerName)
            .Require("name", "Enter a computer name.");
        if (!dlg.Ask("Rename")) return;

        var name = dlg.S("name").Trim();
        var problem = RosterRules.RenameProblem(name, computer.ComputerName, taken);
        if (problem is not null) { MessageBox.Show(this, problem, "Rename computer"); return; }
        if (MessageBox.Show(this, Loc.F($"Rename {computer.ComputerName} to {name.ToUpperInvariant()}? The new name applies after a restart."),
                "Confirm rename", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        var renamed = false;
        await RunOnAsync(targets, $"Rename {computer.ComputerName}", async (c, s) =>
        {
            var answer = await s.CallAsync(Cmd.SystemRename, new JsonObject { [ComputerNameRules.Name] = name });
            Log($"  {c.Name}: renamed to {answer[ComputerNameRules.NewName]?.GetValue<string>()}; restart needed");
            renamed = true;
        });
        if (renamed && MessageBox.Show(this, Loc.F($"{computer.Name} has the new name after a restart. Restart it in 60 seconds?"),
                "Rename computer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await RunOnAsync(targets, $"Restart {computer.Name}", (c, s) => s.CallAsync(Cmd.PowerAction, new JsonObject
            {
                ["action"] = "restart", ["delaySec"] = 60, ["message"] = "This computer is restarting to finish renaming it.",
            }));
    }

    async void AssignRoom_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;

        var existing = Rooms();
        // Pick a room from the list or type a new one: the list holds every room in use, so a typing slip does not make a second room.
        var dlg = new FormDialog("Assign to room", this)
            .Target(targets)
            .Choice("room", existing.Count == 0 ? "_Room (type a name, for example \"Lab 1\")" : "_Room (pick one, or type a new name)",
                existing.Select(room => (room, "")).ToList(), _room)
            .Note("Leave it empty to take the selected computers out of their room.");
        if (!dlg.Ask("Assign")) return;

        RosterRules.AssignRoom(targets, dlg.S("room"));
        SaveComputers();
        RefreshRooms();
        _view.Refresh();
        await CheckAllAsync();
    }

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        SelectAllVisible();
    }

    void SelectAllVisible()
    {
        foreach (var c in _view.Cast<ClientInfo>()) c.IsSelected = true;
        UpdateCollectionState();
    }

    void SelectNone_Click(object sender, RoutedEventArgs e) => ClearSelection();

    void SelectOnline_Click(object sender, RoutedEventArgs e) => SelectOnly(ComputerSelection.IsOnline);

    void SelectOffline_Click(object sender, RoutedEventArgs e) => SelectOnly(ComputerSelection.IsOffline);

    void SelectInvert_Click(object sender, RoutedEventArgs e)
    {
        ComputerSelection.Invert(_view.Cast<ClientInfo>());
        ClientGrid.UnselectAll();
        UpdateCollectionState();
    }

    void SelectOnly(Func<ClientInfo, bool> matches)
    {
        ComputerSelection.Only(_view.Cast<ClientInfo>(), _clients, matches);
        ClientGrid.UnselectAll();
        UpdateCollectionState();
    }

    void ClearSelection()
    {
        foreach (var c in _clients) c.IsSelected = false;
        ClientGrid.UnselectAll();
        UpdateCollectionState();
    }

    void Columns_Click(object sender, RoutedEventArgs e)
    {
        // Keyed by the English name each column remembers: "State" and "Status" are both "Būsena" in Lithuanian.
        var columns = new Dictionary<string, DataGridColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, column) in TableColumns.Named(ClientGrid)) columns.TryAdd(name, column);
        var visible = columns.Where(pair => pair.Value.Visibility == Visibility.Visible).Select(pair => pair.Key).ToList();
        var dlg = new FormDialog("Computer list columns", this)
            .MultiSelect("columns", "Visible columns", columns.Keys.ToList(), visible, presets:
                [("Compact teacher view", ComputerColumnPresets.Teacher), ("Show all columns", columns.Keys.ToList())])
            .Note("Compact teacher view keeps selection, state, computer, room, user and status. Technical columns stay available here. Presets take effect only after Apply.")
            .Note("Select the columns to keep visible. The Select column is always shown.");
        if (!dlg.Ask("Apply")) return;
        var chosen = dlg.S("columns").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (chosen.Count == 0 || (chosen.Count == 1 && chosen.Contains("Select")))
        { MessageBox.Show(this, "Keep at least one information column visible."); return; }
        foreach (var pair in columns)
            pair.Value.Visibility = pair.Key.Equals("Select", StringComparison.OrdinalIgnoreCase) || chosen.Contains(pair.Key)
                ? Visibility.Visible : Visibility.Collapsed;
        _settings.HiddenComputerColumns = columns.Keys
            .Where(column => !column.Equals("Select", StringComparison.OrdinalIgnoreCase) && !chosen.Contains(column))
            .ToList();
        _settings.Save();
    }

    void ApplyComputerColumnSettings()
    {
        var hidden = _settings.HiddenComputerColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, column) in TableColumns.Named(ClientGrid))
        {
            column.Visibility = name.Equals("Select", StringComparison.OrdinalIgnoreCase) || !hidden.Contains(name)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    async void Scan_Click(object sender, RoutedEventArgs e) => await _windowWork.RunAsync(ScanAsync);

    Task ScanAsync(CancellationToken cancellationToken) => _manualDiscovery.ScanAsync(cancellationToken);

    bool IManualDiscoveryView.CanScan => ScanButton.IsEnabled;
    Discovery.NetworkSubnet? IManualDiscoveryView.CurrentNetwork => _networkScope;
    void IManualDiscoveryView.SetScanning(bool scanning) => ScanButton.IsEnabled = !scanning;
    Discovery.SubnetSelection IManualDiscoveryView.Prepare()
    {
        var selection = RefreshNetworkScope();
        RefreshRooms();
        _view.Refresh();
        UpdateCollectionState();
        return selection;
    }
    Task IManualDiscoveryView.RefreshAsync() => CheckAllAsync();
    void IManualDiscoveryView.RefreshRooms() => RefreshRooms();
    void IManualDiscoveryView.Log(string message) => Log(message);
}
