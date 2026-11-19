// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Cached tree of autorun entries from several computers, with per-computer management.</summary>
public sealed class BulkAutorunsWindow : Window
{
    const string MachineLevel = "Machine-wide (elevated)";
    const string UserLevel = "Signed-in user (standard)";

    readonly IReadOnlyList<ClientInfo> _clients;
    readonly HostSettings _settings;
    readonly ComboBox _level = new() { ItemsSource = new[] { MachineLevel, UserLevel }, SelectedIndex = 1, Width = 210 };
    readonly TextBox _filter = new() { Width = 220 };
    readonly TreeView _tree = new();
    readonly TextBlock _status = Ui.StatusText();
    IReadOnlyList<AutorunResult> _results = [];
    string _resultSource = "Saved scan";
    int _loadVersion;

    public BulkAutorunsWindow(IReadOnlyList<ClientInfo> clients, HostSettings settings, Window owner)
    {
        _clients = clients;
        _settings = settings;
        Owner = owner;
        Title = $"Autoruns — {clients.Count} computers";
        Width = 980;
        Height = 620;
        MinWidth = 680;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var saved = _settings.FilterState(nameof(BulkAutorunsWindow));
        _filter.Text = saved.Text;
        var savedScope = saved.Selection("Scope", UserLevel);
        if (_level.Items.Contains(savedScope)) _level.SelectedItem = savedScope;

        var refresh = new Button { Content = "_Recheck", Tag = "\u21BB", ToolTip = "Reload autoruns from every selected computer" };
        refresh.Click += async (_, _) => await LoadAsync();
        _level.SelectionChanged += async (_, _) => await ShowCachedOrLoadAsync();
        _filter.TextChanged += (_, _) => RenderFiltered();
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(Ui.Label("_Scope:", _level));
        top.Children.Add(_level);
        top.Children.Add(Ui.Label("_Filter:", _filter, new Thickness(12, 0, 6, 0)));
        top.Children.Add(_filter);
        top.Children.Add(refresh);
        var (expandAll, collapseAll) = Ui.ExpandCollapseButtons(_tree);
        top.Children.Add(expandAll);
        top.Children.Add(collapseAll);
        Ui.Spaced(top);

        var note = Ui.Explanation("Expand a computer to review its last detected autoruns. Select a computer or entry to manage it; Recheck replaces the saved scan.", new Thickness(0, 0, 0, 8));
        var manage = new Button { Content = "_Manage computer...", Tag = "\u2699", ToolTip = "Open the full autorun manager for the selected computer" };
        manage.Click += (_, _) => OpenManager();
        var remove = new Button { Content = "_Remove selected entry", Tag = "\uD83D\uDDD1", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Remove only the selected autorun hook" };
        remove.Click += async (_, _) => await RemoveSelectedAsync();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(manage);
        actions.Children.Add(remove);
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(actions);
        root.Children.Add(_tree);
        Content = root;

        Loaded += async (_, _) => await ShowCachedOrLoadAsync();
        Closing += (_, _) =>
        {
            saved.Text = _filter.Text;
            saved.Selections["Scope"] = _level.SelectedItem as string ?? UserLevel;
            _settings.Save();
        };
        Ui.RefreshOnF5(this, LoadAsync);
    }

    string Level => _level.SelectedIndex == 1 ? Levels.User : Levels.System;

    async Task ShowCachedOrLoadAsync()
    {
        if (!IsLoaded) return;
        var cached = _clients.Select(client => AutorunSnapshotCache.TryGet(client, Level, out var entries)
            ? new AutorunResult(client, entries, "")
            : null).ToList();
        if (cached.All(result => result is not null))
        {
            Render(cached.Cast<AutorunResult>().ToArray(), "Saved scan");
            return;
        }
        await LoadAsync();
    }

    async Task LoadAsync()
    {
        if (!IsLoaded) return;
        var version = ++_loadVersion;
        _status.Text = $"Loading autoruns from {_clients.Count} computers...";
        var results = await Task.WhenAll(_clients.Select(LoadOneAsync));
        if (version != _loadVersion) return;

        Render(results, "Current scan");
    }

    void Render(IReadOnlyList<AutorunResult> results, string source)
    {
        _results = results;
        _resultSource = source;
        RenderFiltered();
    }

    void RenderFiltered()
    {
        _tree.Items.Clear();
        var filter = _filter.Text.Trim();
        var total = _results.Sum(result => result.Entries.Count);
        var shown = 0;
        foreach (var result in _results.OrderBy(item => item.Client.Name, StringComparer.OrdinalIgnoreCase))
        {
            var entries = result.Entries.Where(entry => Matches(entry, filter)).ToList();
            if (filter.Length > 0 && result.Error.Length == 0 && entries.Count == 0) continue;
            var root = new TreeViewItem
            {
                Header = result.Error.Length > 0
                    ? $"{result.Client.Name} ({result.Client.Address}) — failed"
                    : $"{result.Client.Name} ({result.Client.Address}) — {entries.Count}" +
                      (filter.Length > 0 ? $" matching of {result.Entries.Count}" : " entries"),
                ToolTip = result.Error.Length > 0 ? result.Error : null,
                IsExpanded = _results.Count <= 5 || filter.Length > 0,
                Tag = result.Client,
            };
            if (result.Error.Length > 0)
                root.Items.Add(new TreeViewItem { Header = "Error: " + result.Error });
            else
            {
                foreach (var entry in entries)
                {
                    root.Items.Add(new TreeViewItem
                    {
                        Header = $"[{entry.Kind}] {entry.Name} — {entry.Command}" + (entry.Enabled ? "" : " (disabled)"),
                        ToolTip = $"Source: {entry.Source}" + (entry.MatchedProgram.Length > 0 ? $"\nMatched program: {entry.MatchedProgram}" : ""),
                        Tag = new AutorunSelection(result.Client, entry),
                    });
                }
                shown += entries.Count;
            }
            _tree.Items.Add(root);
        }
        _status.Text = filter.Length == 0
            ? $"{_resultSource}: {total} autorun entries across {_clients.Count} computers."
            : $"{_resultSource}: showing {shown} of {total} autorun entries across {_clients.Count} computers.";
    }

    public static bool Matches(AutorunEntry entry, string filter) => filter.Length == 0 ||
        entry.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        entry.Command.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        entry.Source.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        entry.EnabledText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        entry.MatchedProgram.Contains(filter, StringComparison.OrdinalIgnoreCase);

    async Task<AutorunResult> LoadOneAsync(ClientInfo client)
    {
        try
        {
            using var session = await ClientSession.OpenAsync(client, _settings);
            var data = await session.CallAtAsync(Level, Cmd.AutorunList);
            var entries = data["entries"]!.AsArray().Select(node =>
            {
                var item = node!.AsObject();
                return new AutorunEntry
                {
                    Kind = item["kind"]!.GetValue<string>(),
                    Scope = item["scope"]!.GetValue<string>(),
                    Name = item["name"]!.GetValue<string>(),
                    Command = item["command"]!.GetValue<string>(),
                    Source = item["source"]!.GetValue<string>(),
                    MatchedProgram = item["matchedProgram"]?.GetValue<string>() ?? "",
                    Enabled = item["enabled"]?.GetValue<bool>() ?? true,
                    Raw = item,
                };
            }).OrderBy(entry => entry.Kind).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
            AutorunSnapshotCache.Set(client, Level, entries);
            return new AutorunResult(client, entries, "");
        }
        catch (Exception ex) { return new AutorunResult(client, [], ex.Message); }
    }

    sealed record AutorunResult(ClientInfo Client, List<AutorunEntry> Entries, string Error);
    sealed record AutorunSelection(ClientInfo Client, AutorunEntry Entry);

    ClientInfo? SelectedClient() => (_tree.SelectedItem as TreeViewItem)?.Tag switch
    {
        ClientInfo client => client,
        AutorunSelection selection => selection.Client,
        _ => null,
    };

    void OpenManager()
    {
        var client = SelectedClient();
        if (client is null)
        {
            MessageBox.Show(this, "Select a computer or one of its autorun entries first.");
            return;
        }
        new AutorunsWindow(client, _settings, this).ShowDialog();
        AutorunSnapshotCache.Invalidate([client]);
        _ = LoadAsync();
    }

    async Task RemoveSelectedAsync()
    {
        if ((_tree.SelectedItem as TreeViewItem)?.Tag is not AutorunSelection selection)
        {
            MessageBox.Show(this, "Select an autorun entry first.");
            return;
        }
        if (MessageBox.Show(this, $"Remove [{selection.Entry.Kind}] {selection.Entry.Name} from {selection.Client.Name}?\n\nThe program remains installed.",
                "Confirm autorun removal", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            using var session = await ClientSession.OpenAsync(selection.Client, _settings);
            await session.CallAtAsync(Level, Cmd.AutorunRemove, selection.Entry.Raw);
            AutorunSnapshotCache.Invalidate([selection.Client]);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not remove autorun", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
