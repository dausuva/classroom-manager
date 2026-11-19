// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// Point-in-time details for one computer, opened by double-clicking a row, with the actions that work on one
/// computer at hand, grouped. Each opens the same window as the main toolbar does, for this computer.
/// </summary>
public sealed class ClientInfoWindow : Window
{
    /// <summary>The one-computer actions offered here, by group, named by their English toolbar labels.</summary>
    public static readonly IReadOnlyList<(string Group, string[] Actions)> ActionGroups =
    [
        ("Software", ["Programs...", "Processes...", "Windows Update...", "Terminal...", "Registry..."]),
        ("Files", ["Browse files..."]),
        ("Desktop", ["Managed wallpapers...", "Recent wallpapers...", "Windows themes..."]),
        ("Accounts", ["Local accounts...", "Signed-in accounts...", "App accounts..."]),
    ];

    readonly ClientInfo _client;
    readonly HostSettings _settings;
    readonly TextBlock _status = Ui.StatusText();
    readonly Grid _details = new();

    /// <param name="canRun">Whether an action (by its English toolbar label) is shown in the current mode.</param>
    /// <param name="run">Runs an action for this computer.</param>
    public ClientInfoWindow(ClientInfo client, HostSettings settings, Window owner,
        Func<string, bool>? canRun = null, Action<string>? run = null)
    {
        _client = client; _settings = settings; Owner = owner;
        Title = $"Computer information — {client.Name}";
        Width = 660; Height = 600; MinWidth = 500; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // The labels' own width, so a value starts right after the longest label instead of far to its right.
        _details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var actions = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (run is not null)
            foreach (var (group, labels) in ActionGroups)
            {
                var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
                foreach (var label in labels.Where(label => canRun?.Invoke(label) ?? true))
                {
                    var button = new Button { Content = label, ToolTip = $"{label.TrimEnd('.')} — open it for {client.Name}.", Margin = new Thickness(0, 0, 6, 4) };
                    var action = label;
                    button.Click += (_, _) => run(action);
                    row.Children.Add(button);
                }
                if (row.Children.Count == 0) continue;
                actions.Children.Add(new TextBlock { Text = group, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
                actions.Children.Add(row);
            }

        var refresh = new Button { Content = "_Refresh now", ToolTip = "Refresh the point-in-time information from this computer." };
        refresh.Click += async (_, _) => await RefreshAsync();
        var closeApps = new Button
        {
            Content = "Close _all apps...",
            ToolTip = "Ask every visible Alt+Tab application in the signed-in user's session to close. Background processes and services are not affected.",
            IsEnabled = _client.SupportsCommand(Cmd.ProcCloseApps),
        };
        closeApps.Click += async (_, _) => await CloseAllAppsAsync();
        var close = new Button { Content = "_Close", IsCancel = true };
        close.Click += (_, _) => Close();
        var bottom = Ui.Spaced(new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { refresh, closeApps, close } });

        var body = new StackPanel();
        body.Children.Add(_details);
        body.Children.Add(actions);
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(_status, Dock.Bottom); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(_status); root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Loaded += async (_, _) => await RefreshAsync();
    }

    async Task RefreshAsync()
    {
        _status.Text = "Refreshing computer information...";
        try
        {
            using var session = await ClientSession.OpenAsync(_client, _settings);
            var info = await session.CallAsync(Cmd.Ping);
            _details.Children.Clear();
            _details.RowDefinitions.Clear();
            foreach (var (label, value) in BaseRows(info, _client)) Add(label, value);
            if (_client.SupportsCommand(Cmd.ProcWindows))
            {
                var windows = await session.CallAtAsync(Levels.User, Cmd.ProcWindows);
                Add("Running apps / open windows", $"{windows["appCount"]?.GetValue<int>() ?? 0} / {windows["windowCount"]?.GetValue<int>() ?? 0}");
            }
            if (_client.SupportsCommand(Cmd.WindowsUpdateScan))
            {
                var updates = await session.CallAtAsync(Levels.System, Cmd.WindowsUpdateScan,
                    new JsonObject { ["includeOptional"] = true });
                var list = updates["updates"]?.AsArray() ?? new JsonArray();
                WindowsUpdateSnapshotCache.Set(_client, true, list);
                var titles = list.Select(item => item?["title"]?.GetValue<string>() ?? "").Where(title => title.Length > 0).ToList();
                Add("Windows updates", titles.Count == 0 ? "None reported" : $"{titles.Count} available — {string.Join("; ", titles.Take(3))}{(titles.Count > 3 ? "; ..." : "")}");
            }
            _status.Text = "Information refreshed at " + DateTime.Now.ToString("HH:mm:ss") + ".";
        }
        catch (Exception ex) { _status.Text = "Could not refresh: " + ex.Message; }
    }

    async Task CloseAllAppsAsync()
    {
        if (!_client.SupportsCommand(Cmd.ProcCloseApps))
        {
            MessageBox.Show(this, "Update this Client to a build that supports closing visible applications.", "Close all apps");
            return;
        }
        if (MessageBox.Show(this,
                $"Ask every visible application on {_client.Name} to close?\n\nApplications may show a save prompt on the student computer. Background processes and Windows services are not affected.",
                "Confirm close all apps", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            _status.Text = "Closing visible applications...";
            using var session = await ClientSession.OpenAsync(_client, _settings);
            var result = await session.CallAtAsync(Levels.User, Cmd.ProcCloseApps);
            var apps = result["appCount"]?.GetValue<int>() ?? 0;
            var windows = result["windowCount"]?.GetValue<int>() ?? 0;
            _status.Text = $"Close requested for {apps} app(s) and {windows} window(s). Applications with unsaved work may be waiting for the user.";
        }
        catch (Exception ex) { _status.Text = "Could not close applications: " + ex.Message; }
    }

    /// <summary>
    /// What the computer says about itself, the facts a teacher looks for first at the top: who and where it
    /// is, then how long it has been on, then the software. A fact with no value is left out rather than
    /// shown as an empty line. The MAC address is shown the way Windows prints it.
    /// </summary>
    public static List<(string Label, string Value)> BaseRows(JsonObject info, ClientInfo client)
    {
        string Said(string key, string fallback) => info[key]?.GetValue<string>() is { Length: > 0 } said ? said : fallback;
        var rows = new List<(string Label, string Value)>
        {
            ("Computer", Said("machineName", client.Name)),
            ("Status", client.Status),
            ("Signed-in user", Said("userName", client.User)),
            ("Address", client.Address),
            ("MAC address", ComputerIdentity.FormatMac(Said("mac", client.Mac))),
            ("Uptime", info["uptimeSec"] is JsonNode up ? FormatUptime(up.GetValue<long>()) : client.Uptime),
            ("Windows", Said("windowsVersion", client.WindowsVersion)),
            ("Classroom Client", ClassroomManager.Shared.ReleaseVersions.Display(Said("appVersion", client.AppVersion))),
            ("Protocol", Said("version", client.Version)),
            ("Last seen", client.LastSeen),
        };
        rows.RemoveAll(row => string.IsNullOrWhiteSpace(row.Value));
        return rows;
    }

    /// <summary>
    /// One row of a borderless table: the label, and the value that can be selected and copied. No lines; every second row is shaded like
    /// the rows of the program's other tables, so the eye can follow a line from the label to its value.
    /// </summary>
    void Add(string label, string value)
    {
        var row = _details.RowDefinitions.Count;
        _details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (row % 2 == 1)
        {
            var shade = new Border();
            shade.SetResourceReference(Border.BackgroundProperty, "GridAlternateRowBrush");
            Grid.SetRow(shade, row);
            Grid.SetColumnSpan(shade, 2);
            _details.Children.Add(shade);
        }
        var name = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 4, 12, 4), VerticalAlignment = VerticalAlignment.Top };
        var text = new TextBox { Text = value, IsReadOnly = true, Template = PlainText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 6, 4), Background = null };
        System.Windows.Automation.AutomationProperties.SetName(text, label);
        Grid.SetRow(name, row);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        _details.Children.Add(name);
        _details.Children.Add(text);
    }

    /// <summary>A text box drawn as plain text: the theme's box would put a line under every value.</summary>
    static readonly ControlTemplate PlainText = new(typeof(TextBox))
    {
        VisualTree = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost"),
    };

    static string FormatUptime(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m" : $"{span.Hours}h {span.Minutes}m";
    }
}
