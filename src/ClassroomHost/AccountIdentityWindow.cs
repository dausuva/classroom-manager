// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

sealed class StoredCredential
{
    public string Target { get; init; } = "";
}

public sealed class AccountIdentityWindow : Window
{
    ClientInfo _client;
    readonly HostSettings _settings;
    readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    readonly TextBlock _status = Ui.StatusText();
    readonly TextBlock _identity = new() { TextWrapping = TextWrapping.Wrap };

    public AccountIdentityWindow(ClientInfo client, HostSettings settings, Window owner)
    {
        _client = client;
        _settings = settings;
        Owner = owner;
        Title = $"Signed-in account — {client.Name}";
        Width = 760;
        Height = 500;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Stored app/account target", Width = 560, DisplayMemberBinding = new Binding(nameof(StoredCredential.Target)) });
        _list.View = view;
        var refresh = Button("_Refresh", async (_, _) => await LoadAsync());
        var remove = Button("Remove selected _app account...", async (_, _) => await RemoveAsync());
        var accountSettings = Button("Open Windows _account settings", async (_, _) => await OpenSettingsAsync());
        var close = Button("_Close", (_, _) => Close());
        close.IsCancel = true;
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in ComputerPicker.ForRow(new ComputerPicker(client, SwitchTo))) top.Children.Add(item);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in new[] { refresh, remove, accountSettings, close }) buttons.Children.Add(button);
        var note = Ui.Explanation("This view is separate from Local accounts: it shows the active signed-in Windows identity and that user's Windows Credential Manager targets without exposing passwords. Removing a selected target signs that app/account out where Windows supports it. Detaching a personal Microsoft account from the local profile must be completed in Windows account settings.", new Thickness(0, 0, 0, 8));
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_identity, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(note);
        root.Children.Add(_identity);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
        Ui.RefreshOnF5(this, LoadAsync);
    }

    static Button Button(string content, RoutedEventHandler click)
    {
        var button = new Button { Content = content, MinWidth = 130, ToolTip = content.Replace("_", "") };
        button.Click += click;
        return button;
    }

    /// <summary>The computer was changed in the picker: show it, with an empty list until its data arrives.</summary>
    async void SwitchTo(ClientInfo computer)
    {
        _client = computer;
        Title = $"Signed-in account — {computer.Name}";
        _identity.Text = "";
        _list.ItemsSource = new List<StoredCredential>();
        _status.Text = ComputerPicker.NowShowing(computer);
        await LoadAsync();
    }

    async Task LoadAsync()
    {
        var computer = _client;
        try
        {
            _status.Text = "Checking the signed-in Windows account...";
            using var session = await ClientSession.OpenAsync(computer, _settings);
            var data = await session.CallAtAsync(Levels.User, Cmd.AccountIdentityList);
            if (computer != _client) return;   // another computer was chosen meanwhile
            var microsoft = data["microsoftAccountConnected"]?.GetValue<bool>() == true;
            _identity.Text = $"Active signed-in Windows user: {data["user"]?.GetValue<string>()}\nWindows identity: {data["identity"]?.GetValue<string>()}\nMicrosoft account indication: {(microsoft ? "detected" : "not detected")}";
            _list.ItemsSource = data["credentials"]!.AsArray().Select(node => new StoredCredential
            {
                Target = node!["target"]!.GetValue<string>(),
            }).ToList();
            _status.Text = $"{_list.Items.Count} stored app/account target(s).";
        }
        catch (Exception ex) { if (computer == _client) _status.Text = "Could not inspect the signed-in account: " + ex.Message; }
    }

    async Task RemoveAsync()
    {
        var selected = _list.SelectedItems.Cast<StoredCredential>().ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "Select one or more stored app/account targets first."); return; }
        if (MessageBox.Show(this, $"Remove {selected.Count} selected stored credential(s) from {_client.Name}?\n\nThis may sign the user out of the associated apps.",
                "Remove app accounts", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            using var session = await ClientSession.OpenAsync(_client, _settings);
            foreach (var item in selected)
                await session.CallAtAsync(Levels.User, Cmd.AccountIdentityRemove, new JsonObject { ["target"] = item.Target });
            await LoadAsync();
        }
        catch (Exception ex) { _status.Text = "Could not remove one or more app credentials: " + ex.Message; }
    }

    async Task OpenSettingsAsync()
    {
        try
        {
            using var session = await ClientSession.OpenAsync(_client, _settings);
            await session.CallAtAsync(Levels.User, Cmd.AccountIdentityOpenSettings);
            _status.Text = "Windows account settings opened on the selected computer.";
        }
        catch (Exception ex) { _status.Text = "Could not open Windows account settings: " + ex.Message; }
    }
}
