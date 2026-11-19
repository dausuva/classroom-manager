// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class AccountEntry
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool Enabled { get; init; }
    public bool Locked { get; init; }
    public bool Admin { get; init; }
    public string EnabledText => Enabled ? "enabled" : "disabled";
    public string AdminText => Admin ? "yes" : "";
    public string LockedText => Locked ? "yes" : "";
    public bool BuiltIn { get; init; }
}

/// <summary>Lists local (SAM) accounts on one computer and lets you create, reset, enable/disable or delete one.</summary>
public sealed class AccountsWindow : Window
{
    ClientInfo _client;
    readonly HostSettings _settings;
    readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    readonly TextBlock _status = Ui.StatusText();
    List<AccountEntry> _all = new();
    int _loadVersion;

    public AccountsWindow(ClientInfo client, HostSettings settings, Window owner)
    {
        _client = client;
        _settings = settings;
        Owner = owner;
        Title = $"Local accounts - {client.Name}";
        Width = 720;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new GridView();
        grid.Columns.Add(Col("Name", "Name", 160));
        grid.Columns.Add(Col("Display name", "DisplayName", 180));
        grid.Columns.Add(Col("Status", "EnabledText", 80));
        grid.Columns.Add(Col("Locked out", "LockedText", 80));
        grid.Columns.Add(Col("Admin", "AdminText", 60));
        _list.View = grid;

        var refresh = new Button { Content = "_Refresh" };
        refresh.Click += async (_, _) => await LoadAsync();
        var identity = new Button
        {
            Content = "_Signed-in accounts...",
            ToolTip = "Inspect the active Windows identity and removable stored app/account credentials.",
            Margin = new Thickness(6, 0, 0, 0),
        };
        identity.Click += (_, _) => new AccountIdentityWindow(_client, _settings, this).ShowDialog();
        var create = new Button { Content = "_Create...", Margin = new Thickness(6, 0, 0, 0) };
        create.Click += async (_, _) => await CreateAsync();
        var resetPassword = new Button { Content = "Reset _password...", Margin = new Thickness(6, 0, 0, 0) };
        resetPassword.Click += async (_, _) => await ResetPasswordAsync();
        var toggle = new Button { Content = "_Enable/disable", Margin = new Thickness(6, 0, 0, 0) };
        toggle.Click += async (_, _) => await ToggleAsync();
        var delete = new Button { Content = "_Delete...", Margin = new Thickness(6, 0, 0, 0) };
        delete.Click += async (_, _) => await DeleteAsync();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in ComputerPicker.ForRow(new ComputerPicker(client, SwitchTo))) top.Children.Add(item);
        top.Children.Add(refresh);
        top.Children.Add(create);
        top.Children.Add(identity);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(resetPassword);
        bottom.Children.Add(toggle);
        bottom.Children.Add(delete);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_status);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
        Ui.RefreshOnF5(this, LoadAsync);
    }

    static GridViewColumn Col(string header, string path, double width) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new Binding(path) };

    /// <summary>The computer was changed in the picker: show it, with an empty list until its data arrives.</summary>
    async void SwitchTo(ClientInfo computer)
    {
        _client = computer;
        Title = $"Local accounts - {computer.Name}";
        _all = new();
        _list.ItemsSource = new List<AccountEntry>();
        _status.Text = ComputerPicker.NowShowing(computer);
        await LoadAsync();
    }

    async Task LoadAsync()
    {
        var loadVersion = ++_loadVersion;
        try
        {
            _status.Text = "Loading...";
            using var s = await ClientSession.OpenAsync(_client, _settings);
            var data = await s.CallAsync(Cmd.AccountList);
            if (loadVersion != _loadVersion) return;
            _all = data["accounts"]!.AsArray().Select(p => new AccountEntry
            {
                Name = p!["name"]!.GetValue<string>(),
                DisplayName = p["displayName"]!.GetValue<string>(),
                Enabled = p["enabled"]!.GetValue<bool>(),
                Locked = p["locked"]!.GetValue<bool>(),
                Admin = p["admin"]!.GetValue<bool>(),
                BuiltIn = p["builtIn"]?.GetValue<bool>() ?? IsKnownBuiltInName(p["name"]!.GetValue<string>()),
            }).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Ui.ReplaceItemsPreservingSelection(_list, _all, a => a.Name);
            _status.Text = $"{_all.Count} account(s)";
        }
        catch (Exception ex) { if (loadVersion == _loadVersion) _status.Text = "Error: " + ex.Message; }
    }

    static bool IsKnownBuiltInName(string name) => name.Equals("Administrator", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Guest", StringComparison.OrdinalIgnoreCase) || name.Equals("DefaultAccount", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase);

    AccountEntry? OneSelected()
    {
        var items = _list.SelectedItems.Cast<AccountEntry>().ToList();
        if (items.Count != 1)
        {
            MessageBox.Show(this, "Select exactly one account first.");
            return null;
        }
        return items[0];
    }

    async Task CreateAsync()
    {
        var dlg = new FormDialog("Create local account", this)
            .Text("name", "Account name")
            .Text("password", "Password", password: true)
            .Check("admin", "Make this account a local administrator")
            .Check("mustChange", "Require a password change at next sign-in", true)
            .Note("Creates a local (SAM) account - not a Microsoft account and not a domain account.");
        if (!dlg.Ask("Create")) return;

        var name = dlg.S("name");
        var password = dlg.S("password");
        if (name.Length == 0 || password.Length == 0) { MessageBox.Show(this, "Enter both a name and a password."); return; }

        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            await s.CallAsync(Cmd.AccountCreate, new JsonObject
            {
                ["name"] = name,
                ["password"] = password,
                ["admin"] = dlg.B("admin"),
                ["mustChangePassword"] = dlg.B("mustChange"),
            });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not create the account", MessageBoxButton.OK, MessageBoxImage.Warning); }
        await LoadAsync();
    }

    async Task ResetPasswordAsync()
    {
        var account = OneSelected();
        if (account is null) return;

        var dlg = new FormDialog($"Reset password - {account.Name}", this)
            .Text("password", "New password", password: true)
            .Check("mustChange", "Require a password change at next sign-in", true);
        if (!dlg.Ask("Reset")) return;
        var password = dlg.S("password");
        if (password.Length == 0) { MessageBox.Show(this, "Enter a password."); return; }

        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            await s.CallAsync(Cmd.AccountSetPassword, new JsonObject
            {
                ["name"] = account.Name,
                ["password"] = password,
                ["mustChangePassword"] = dlg.B("mustChange"),
            });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not reset the password", MessageBoxButton.OK, MessageBoxImage.Warning); }
        await LoadAsync();
    }

    async Task ToggleAsync()
    {
        var account = OneSelected();
        if (account is null) return;
        var enable = !account.Enabled;

        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            await s.CallAsync(enable ? Cmd.AccountEnable : Cmd.AccountDisable, new JsonObject { ["name"] = account.Name });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, $"Could not {(enable ? "enable" : "disable")} the account", MessageBoxButton.OK, MessageBoxImage.Warning); }
        await LoadAsync();
    }

    async Task DeleteAsync()
    {
        var account = OneSelected();
        if (account is null) return;
        if (account.BuiltIn)
        {
            MessageBox.Show(this, "Built-in Windows accounts can be enabled or disabled, but Windows does not allow them to be deleted.",
                "Built-in account", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"Delete the local account '{account.Name}' on {_client.Name}? This cannot be undone.",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            using var s = await ClientSession.OpenAsync(_client, _settings);
            await s.CallAsync(Cmd.AccountDelete, new JsonObject { ["name"] = account.Name });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not delete the account", MessageBoxButton.OK, MessageBoxImage.Warning); }
        await LoadAsync();
    }
}
