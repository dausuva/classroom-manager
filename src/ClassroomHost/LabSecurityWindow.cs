// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ClassroomManager.Host;

/// <summary>
/// "Secure this lab" (T-194): asks the computers it is given and lists every property of the security plan as a row, red or yellow where a
/// computer is weaker than it should be, with the command that fixes it. It only looks; nothing here changes a computer.
/// </summary>
public sealed class LabSecurityWindow : Window
{
    sealed record Row(string State, string Title, string Detail, string Fix, Brush Colour);

    readonly HostSettings _settings;
    readonly IReadOnlyList<ClientInfo> _clients;
    readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        SelectionMode = DataGridSelectionMode.Single,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column,
    };
    readonly TextBlock _status = Ui.StatusText();
    readonly Button _recheck;
    IReadOnlyList<LabSecurityRow> _rows = [];
    bool _running;

    public LabSecurityWindow(Window owner, HostSettings settings, IReadOnlyList<ClientInfo> clients)
    {
        _settings = settings;
        _clients = clients;
        Owner = owner;
        Title = $"Secure this lab - {clients.Count} computer(s)";
        Width = 1000;
        Height = 560;
        MinWidth = 700;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        System.Windows.Automation.AutomationProperties.SetName(_grid, "Security of the lab");

        _grid.Columns.Add(Column("State", nameof(Row.State), 90, colour: true));
        _grid.Columns.Add(Column("What", nameof(Row.Title), 180));
        _grid.Columns.Add(Column("What it means", nameof(Row.Detail), 460, wrap: true));
        _grid.Columns.Add(Column("To fix it", nameof(Row.Fix), 300, wrap: true));

        _recheck = ActionButton("_Recheck", "↻", "Recheck — ask the computers again.");
        _recheck.Click += async (_, _) => await LoadAsync();
        var copy = ActionButton("_Copy as text", FluentIcons.Copy, "Copy as text — put the report on the clipboard, for a message to whoever looks after the lab.");
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(LabSecurityReport.Text(_rows));
            _status.Text = "The report was copied to the clipboard.";
        };
        var close = ActionButton("_Close", "✕", "Close this window.");
        close.IsCancel = true;
        close.Click += (_, _) => Close();

        var note = Ui.Explanation(
            "Each row is a property the lab should have. Red is a problem, yellow a weakness, blue only information. The window asks every computer " +
            "in the list and changes nothing; the command in the last column fixes the row. The same report is printed by ClassroomCli security.",
            new Thickness(0, 0, 0, 8));
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { _recheck, copy, close }) actions.Children.Add(button);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(actions);
        root.Children.Add(_grid);
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
        Ui.RefreshOnF5(this, LoadAsync);
    }

    static DataGridTextColumn Column(string header, string path, double width, bool colour = false, bool wrap = false)
    {
        var column = new DataGridTextColumn { Header = header, Width = width, Binding = new Binding(path) };
        if (colour || wrap)
        {
            var style = new Style(typeof(TextBlock));
            if (wrap) style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            if (colour)
            {
                style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(Row.Colour))));
                style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            }
            column.ElementStyle = style;
        }
        return column;
    }

    static Button ActionButton(string content, string icon, string tooltip) => new()
    {
        Content = content,
        Tag = icon,
        ToolTip = tooltip,
        MinWidth = 110,
        Margin = new Thickness(8, 0, 0, 0),
    };

    async Task LoadAsync()
    {
        if (_running) return;
        _running = true;
        _recheck.IsEnabled = false;
        try
        {
            _status.Text = $"Asking {_clients.Count} computer(s)...";
            var computers = await SecurityCli.GatherAsync(_settings, _clients, parallel: 8, CancellationToken.None);
            _rows = LabSecurityReport.Build(_settings, computers);
            _grid.ItemsSource = _rows.Select(row => new Row(Describe(row.State), row.Title, row.Detail, row.Action, Colour(row.State))).ToList();
            var overall = LabSecurityReport.Overall(_rows);
            _status.Text = overall switch
            {
                LabState.Ok => "Nothing to fix.",
                LabState.Info => "Nothing to fix; some rows are only information.",
                LabState.Warning => "Some computers are weaker than they should be; see the yellow rows.",
                _ => "There is a problem; see the red rows.",
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = "The report could not be made: " + ex.Message;
        }
        finally
        {
            _running = false;
            _recheck.IsEnabled = true;
        }
    }

    static string Describe(LabState state) => state switch
    {
        LabState.Ok => "OK",
        LabState.Info => "Information",
        LabState.Warning => "Weakness",
        _ => "Problem",
    };

    static Brush Colour(LabState state) => state switch
    {
        LabState.Ok => Brushes.SeaGreen,
        LabState.Info => Brushes.SteelBlue,
        LabState.Warning => Brushes.DarkGoldenrod,
        _ => Brushes.Firebrick,
    };
}
