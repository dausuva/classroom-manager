// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Read-only comparison of local SAM accounts across selected computers.</summary>
public sealed class BulkAccountsWindow : Window
{
    const string AllAccounts = "All accounts";
    const string DifferencesOrAttention = "Differences / attention";

    readonly IReadOnlyList<ClientInfo> _clients;
    readonly HostSettings _settings;
    readonly ComboBox _comparison = new()
    {
        ItemsSource = new[] { AllAccounts, DifferencesOrAttention },
        SelectedIndex = 0,
        Width = 175,
    };
    readonly TextBox _filter = new() { Width = 220 };
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
    readonly Button _refresh;
    List<ComparedAccount> _all = [];
    string _availability = "";
    bool _running;
    int _loadVersion;

    public BulkAccountsWindow(IReadOnlyList<ClientInfo> clients, HostSettings settings, Window owner)
    {
        _clients = clients;
        _settings = settings;
        Owner = owner;
        Title = $"Local accounts inventory — {clients.Count} computers";
        Width = 1180;
        Height = 680;
        MinWidth = 820;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var saved = settings.FilterState(nameof(BulkAccountsWindow));
        _filter.Text = saved.Text;
        var savedComparison = saved.Selection("Comparison", AllAccounts);
        if (_comparison.Items.Contains(savedComparison)) _comparison.SelectedItem = savedComparison;

        AddColumn("Account", nameof(ComparedAccount.Name), 150);
        AddColumn("Display name(s)", nameof(ComparedAccount.DisplayNames), 175);
        AddColumn("Present", nameof(ComparedAccount.Present), 110);
        AddColumn("Enabled", nameof(ComparedAccount.Enabled), 75);
        AddColumn("Admin", nameof(ComparedAccount.Administrators), 75);
        AddColumn("Locked", nameof(ComparedAccount.Locked), 70);
        AddColumn("Built-in", nameof(ComparedAccount.BuiltIn), 65);
        AddColumn("Computer states", nameof(ComparedAccount.Computers), 300);
        AddColumn("Missing from", nameof(ComparedAccount.MissingComputers), 170);
        AddColumn("Comparison", nameof(ComparedAccount.Comparison), 220);

        _refresh = new Button
        {
            Content = "_Recheck",
            Tag = "↻",
            ToolTip = "Reload local accounts from every selected computer.",
        };
        _refresh.Click += async (_, _) => await LoadAsync();
        var close = new Button { Content = "_Close", Tag = "✕", IsCancel = true };
        close.Click += (_, _) => Close();
        _comparison.SelectionChanged += (_, _) => ApplyFilter();
        _filter.TextChanged += (_, _) => ApplyFilter();

        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        controls.Children.Add(Ui.Label("_Show:", _comparison));
        controls.Children.Add(_comparison);
        controls.Children.Add(Ui.Label("_Filter:", _filter, new Thickness(12, 0, 6, 0)));
        controls.Children.Add(_filter);
        controls.Children.Add(_refresh);
        Ui.Spaced(controls);

        var note = Ui.Explanation(
            "This read-only inventory groups local accounts by name across the selected computers. " +
            "Unavailable PCs are not counted as missing. Select one computer in the main window to create, reset, enable, disable, or delete accounts.",
            new Thickness(0, 0, 0, 8));
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        actions.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(controls, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(controls);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(actions);
        root.Children.Add(_grid);
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, _) =>
        {
            saved.Text = _filter.Text;
            saved.Selections["Comparison"] = _comparison.SelectedItem as string ?? AllAccounts;
            _settings.Save();
        };
        Ui.RefreshOnF5(this, LoadAsync);
    }

    void AddColumn(string header, string path, double width) =>
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Width = width,
            Binding = new Binding(path),
        });

    async Task LoadAsync()
    {
        if (_running) return;
        var version = ++_loadVersion;
        SetRunning(true);
        try
        {
            var completed = 0;
            _status.Text = $"Checking local accounts on {_clients.Count} computers...";
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
                .ToDictionary(result => result.Client, result => (IReadOnlyList<AccountEntry>)result.Accounts);
            _all = AccountInventoryComparison.Build(_clients, successful);
            var failures = results.Where(result => result.Error.Length > 0).ToList();
            _availability = $"Checked {successful.Count} of {_clients.Count} computers." +
                (failures.Count == 0 ? "" : " Unavailable: " + string.Join("; ",
                    failures.Select(result => $"{result.Client.Name}: {result.Error}")));
            ApplyFilter();
        }
        finally { SetRunning(false); }
    }

    async Task<AccountInventoryResult> LoadOneAsync(ClientInfo client)
    {
        try
        {
            if (client.ExclusiveOperation.Length > 0)
                return new(client, [], client.ExclusiveOperation);
            using var session = await ClientSession.OpenAsync(client, _settings);
            var data = await session.CallAsync(Cmd.AccountList);
            var accounts = data["accounts"]!.AsArray().Select(node =>
            {
                var item = node!.AsObject();
                var name = item["name"]?.GetValue<string>() ?? "";
                return new AccountEntry
                {
                    Name = name,
                    DisplayName = item["displayName"]?.GetValue<string>() ?? "",
                    Enabled = item["enabled"]?.GetValue<bool>() ?? true,
                    Locked = item["locked"]?.GetValue<bool>() ?? false,
                    Admin = item["admin"]?.GetValue<bool>() ?? false,
                    BuiltIn = item["builtIn"]?.GetValue<bool>() ?? IsKnownBuiltInName(name),
                };
            }).OrderBy(account => account.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return new(client, accounts, "");
        }
        catch (Exception ex) { return new(client, [], ex.Message); }
    }

    void ApplyFilter()
    {
        var text = _filter.Text.Trim();
        var attentionOnly = (_comparison.SelectedItem as string) == DifferencesOrAttention;
        var shown = _all.Where(item =>
            (!attentionOnly || item.HasDifferenceOrAttention) &&
            (text.Length == 0 || item.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
             item.DisplayNames.Contains(text, StringComparison.OrdinalIgnoreCase) ||
             item.Computers.Contains(text, StringComparison.OrdinalIgnoreCase) ||
             item.MissingComputers.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
        _grid.ItemsSource = shown;
        if (!_running)
            _status.Text = $"Showing {shown.Count} of {_all.Count} account groups. {_availability}";
    }

    void SetRunning(bool value)
    {
        _running = value;
        _refresh.IsEnabled = !value;
        _comparison.IsEnabled = !value;
        if (!value) ApplyFilter();
    }

    static bool IsKnownBuiltInName(string name) =>
        name.Equals("Administrator", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Guest", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DefaultAccount", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase);

    sealed record AccountInventoryResult(ClientInfo Client, List<AccountEntry> Accounts, string Error);
}
