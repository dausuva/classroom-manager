// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>One app account on one computer, as the App accounts window lists it.</summary>
public sealed record AppAccountEntry(string Computer, string User, string App, string Account, string Detail);

/// <summary>
/// The accounts that apps on the selected computers are signed in to: OneDrive, Microsoft 365, Windows'
/// own app accounts, Teams, Dropbox, Google Drive, Steam, Spotify and browser profiles (see AppAccounts).
/// </summary>
public sealed class AppAccountsWindow : Window
{
    readonly IReadOnlyList<ClientInfo> _targets;
    readonly HostSettings _settings;
    readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    readonly TextBlock _status = Ui.StatusText();
    List<AppAccountEntry> _entries = [];

    public AppAccountsWindow(List<ClientInfo> targets, HostSettings settings, Window owner)
    {
        _targets = targets;
        _settings = settings;
        Owner = owner;
        Title = targets.Count == 1 ? $"App accounts — {targets[0].Name}" : $"App accounts — {targets.Count} computers";
        Width = 960;
        Height = 560;
        MinWidth = 640;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var columns = new GridView();
        foreach (var (header, path, width) in new[]
                 {
                     ("Computer", nameof(AppAccountEntry.Computer), 120d), ("User", nameof(AppAccountEntry.User), 110d),
                     ("App", nameof(AppAccountEntry.App), 220d), ("Account", nameof(AppAccountEntry.Account), 220d),
                     ("Details", nameof(AppAccountEntry.Detail), 260d),
                 })
            columns.Columns.Add(new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new Binding(path) });
        _list.View = columns;

        var refresh = new Button { Content = "_Refresh", Tag = FluentIcons.Refresh, ToolTip = "Refresh (F5) — read the app accounts again from the selected computers." };
        refresh.Click += async (_, _) => await LoadAsync();
        var copy = new Button { Content = "_Copy", Tag = FluentIcons.Copy, ToolTip = "Copy — put the selected rows, or all of them when none is selected, on the clipboard as a table." };
        copy.Click += (_, _) => Copy();
        var close = new Button { Content = "C_lose", Tag = FluentIcons.Close, IsCancel = true };
        close.Click += (_, _) => Close();
        var buttons = Ui.Spaced(new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { refresh, copy, close } });

        var note = Ui.Explanation(
            "Read from the signed-in user of each computer: the account names that apps keep in plain view, never passwords. " +
            "Apps that keep their account encrypted, such as Discord, are listed as in use without a name.",
            new Thickness(0, 0, 0, 8));
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
        Ui.RefreshOnF5(this, LoadAsync);
    }

    async Task LoadAsync()
    {
        _status.Text = $"Reading app accounts from {_targets.Count} computer(s)...";
        var answers = await Task.WhenAll(_targets.Select(async client =>
        {
            if (client.User.Length == 0) return (Client: client, Entries: new List<AppAccountEntry>(), Error: "nobody is signed in");
            try
            {
                using var session = await ClientSession.OpenAsync(client, _settings);
                var accounts = AppAccounts.From(await session.CallAtAsync(Levels.User, Cmd.AppAccountsList));
                return (Client: client, Entries: accounts.Select(account => new AppAccountEntry(client.Name, client.User, account.App, account.Name, account.Detail)).ToList(), Error: "");
            }
            catch (Exception ex) { return (Client: client, Entries: new List<AppAccountEntry>(), Error: ex.Message); }
        }));
        _entries = answers.SelectMany(answer => answer.Entries).ToList();
        _list.ItemsSource = _entries;
        var failed = answers.Where(answer => answer.Error.Length > 0).ToList();
        var notRead = string.Join("; ", failed.Select(answer => $"{answer.Client.Name}: {answer.Error}"));
        _status.Text = failed.Count == 0
            ? $"{_entries.Count} app account(s) on {answers.Length} computer(s)."
            : $"{_entries.Count} app account(s) on {answers.Length - failed.Count} computer(s). Not read: {notRead}";
    }

    void Copy()
    {
        var rows = _list.SelectedItems.Count > 0 ? _list.SelectedItems.Cast<AppAccountEntry>().ToList() : _entries;
        var lines = new[] { "Computer\tUser\tApp\tAccount\tDetails" }
            .Concat(rows.Select(row => string.Join('\t', row.Computer, row.User, row.App, row.Account, row.Detail)));
        try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); }
        catch (System.Runtime.InteropServices.COMException ex) { _status.Text = "Could not copy: " + ex.Message; return; }
        _status.Text = $"{rows.Count} row(s) copied.";
    }
}
