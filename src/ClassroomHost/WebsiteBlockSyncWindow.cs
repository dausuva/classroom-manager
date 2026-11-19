// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class WebsiteBlockChoice
{
    public bool Selected { get; set; }
    public string Domain { get; init; } = "";
    public string Computers { get; init; } = "";
}

/// <summary>Inventories app-owned website restrictions and enforces one Host-owned baseline.</summary>
public sealed class WebsiteBlockSyncWindow : Window
{
    readonly IReadOnlyList<ClientInfo> _clients;
    readonly HostSettings _settings;
    readonly DataGrid _list = new()
    {
        AutoGenerateColumns = false,
        CanUserAddRows = false,
        SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column,
    };
    readonly TextBlock _status = Ui.StatusText();
    readonly Button _refresh = new() { Content = "↻ _Scan selected computers" };
    readonly Button _save = new() { Content = "★ _Save selected as default", Margin = new Thickness(6, 0, 0, 0) };
    readonly Button _apply = new() { Content = "🛡 _Block default on selected", Margin = new Thickness(6, 0, 0, 0) };
    List<WebsiteBlockChoice> _choices = [];
    bool _busy;

    public WebsiteBlockSyncWindow(IReadOnlyList<ClientInfo> clients, HostSettings settings, Window owner)
    {
        _clients = clients;
        _settings = settings;
        Owner = owner;
        Title = "Website blocklist sync";
        Width = 820;
        Height = 560;
        MinWidth = 620;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "Default",
            Binding = new Binding(nameof(WebsiteBlockChoice.Selected))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            },
            Width = 75,
        });
        _list.Columns.Add(new DataGridTextColumn
        {
            Header = "Website",
            Binding = new Binding(nameof(WebsiteBlockChoice.Domain)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        });
        _list.Columns.Add(new DataGridTextColumn
        {
            Header = "Computers",
            Binding = new Binding(nameof(WebsiteBlockChoice.Computers)),
            Width = 310,
        });

        _refresh.Click += async (_, _) => await ScanAsync();
        _save.Click += (_, _) => SaveDefault();
        _apply.Click += async (_, _) => await ApplyDefaultAsync();

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_save);
        buttons.Children.Add(_apply);
        var note = Ui.Explanation(
            "Scan reads only the computers selected when this window was opened and selects domains already in this Host's default. Saving is allowed with no domains selected. “Block default” turns off and clears Exam mode on each selected computer, then replaces every Classroom Manager-owned website entry with only the current default. Unmanaged hosts-file lines are preserved.",
            new Thickness(0, 0, 0, 8));
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(_list);
        Content = root;

        Loaded += async (_, _) => await ScanAsync();
        Ui.RefreshOnF5(this, ScanAsync);
    }

    async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true, $"Scanning {_clients.Count} computer(s)...");
        try
        {
            var results = await Task.WhenAll(_clients.Select(ReadOneAsync));
            var computersByDomain = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var domain in _settings.DefaultWebsiteBlocklist)
                computersByDomain.TryAdd(domain, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            foreach (var result in results.Where(result => result.Error.Length == 0))
            {
                foreach (var domain in result.Domains)
                {
                    if (!computersByDomain.TryGetValue(domain, out var computers))
                        computersByDomain[domain] = computers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    computers.Add(result.Client.Name);
                }
            }

            var defaults = _settings.DefaultWebsiteBlocklist.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _choices = computersByDomain.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new WebsiteBlockChoice
                {
                    Selected = defaults.Contains(pair.Key),
                    Domain = pair.Key,
                    Computers = pair.Value.Count == 0
                        ? "Default only"
                        : $"{pair.Value.Count}: {string.Join(", ", pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}",
                }).ToList();
            _list.ItemsSource = _choices;
            var failed = results.Count(result => result.Error.Length > 0);
            var examEntries = results.Where(result => result.Error.Length == 0).Sum(result => result.ExamDomainCount);
            _status.Text = $"{_choices.Count} unique website(s); {_clients.Count - failed} computer(s) read, {failed} failed; {examEntries} active Exam-mode website entr{(examEntries == 1 ? "y" : "ies")} found.";
        }
        finally { SetBusy(false); }
    }

    async Task<WebsiteScanResult> ReadOneAsync(ClientInfo client)
    {
        try
        {
            using var session = await ClientSession.OpenAsync(client, _settings);
            var data = await session.CallAsync(Cmd.BlockHostsList);
            var source = data["effectiveDomains"] as JsonArray ?? data["domains"]?.AsArray() ?? new JsonArray();
            var domains = source.Select(node => node?.GetValue<string>() ?? "")
                .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new WebsiteScanResult(client, domains, (data["examDomains"] as JsonArray)?.Count ?? 0, "");
        }
        catch (Exception ex) { return new WebsiteScanResult(client, [], 0, ex.Message); }
    }

    void SaveDefault()
    {
        _list.CommitEdit(DataGridEditingUnit.Cell, true);
        _list.CommitEdit(DataGridEditingUnit.Row, true);
        _settings.DefaultWebsiteBlocklist = _choices.Where(choice => choice.Selected)
            .Select(choice => choice.Domain).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        _settings.Save();
        _status.Text = $"Saved {_settings.DefaultWebsiteBlocklist.Count} website(s) as the Host default.";
    }

    async Task ApplyDefaultAsync()
    {
        if (_busy) return;
        if (_settings.ExamModeEnabled)
        {
            MessageBox.Show(this, "Turn off this Host's active Exam mode before enforcing the default blocklist.", "Website blocklist sync");
            return;
        }
        var count = _settings.DefaultWebsiteBlocklist.Count;
        if (MessageBox.Show(this,
                $"Replace every Classroom Manager-owned website block on {_clients.Count} computer(s) with the Host default ({count} website(s))?\n\n" +
                "This turns off and clears the entire Exam-mode overlay, then applies only the current default website list. Unmanaged hosts-file entries are preserved.",
                "Confirm blocklist synchronization", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        SetBusy(true, $"Applying the default to {_clients.Count} computer(s)...");
        try
        {
            using var gate = new SemaphoreSlim(8);
            var results = await Task.WhenAll(_clients.Select(async client =>
            {
                await gate.WaitAsync();
                try
                {
                    if (client.Version.Length > 0 && !client.SupportsCommand(Cmd.BlockHostsReset))
                        return $"{client.Name}: update Client to protocol 1.5 with website reset support";
                    using var session = await ClientSession.OpenAsync(client, _settings);
                    var domains = new JsonArray(_settings.DefaultWebsiteBlocklist.Select(value => (JsonNode)value).ToArray());
                    var response = await session.CallAsync(Cmd.BlockHostsReset, new JsonObject { ["domains"] = domains });
                    if (response["effectiveCount"] is JsonNode effective)
                        client.BlockedWebsiteCount = effective.GetValue<int>();
                    return "";
                }
                catch (Exception ex) { return $"{client.Name}: {ex.Message}"; }
                finally { gate.Release(); }
            }));
            var failures = results.Where(value => value.Length > 0).ToList();
            _status.Text = failures.Count == 0
                ? $"Default blocklist applied to all {_clients.Count} computer(s)."
                : $"Default applied to {_clients.Count - failures.Count} computer(s); {failures.Count} failed.";
            if (failures.Count > 0)
                MessageBox.Show(this, string.Join("\n\n", failures), "Some computers were not synchronized", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        _refresh.IsEnabled = !busy;
        _save.IsEnabled = !busy;
        _apply.IsEnabled = !busy;
        if (status is not null) _status.Text = status;
    }

    sealed record WebsiteScanResult(ClientInfo Client, List<string> Domains, int ExamDomainCount, string Error);
}
