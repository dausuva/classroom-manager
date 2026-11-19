// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Read-only signed-in identity and Credential Manager inventory for selected computers.</summary>
public sealed class BulkAccountIdentityWindow : Window
{
    readonly IReadOnlyList<ClientInfo> _clients;
    readonly HostSettings _settings;
    readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        SelectionMode = DataGridSelectionMode.Extended,
        SelectionUnit = DataGridSelectionUnit.FullRow,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        RowHeight = double.NaN,
    };
    readonly TextBlock _status = Ui.StatusText();
    readonly Button _refresh;
    readonly Button _remove;
    readonly ProgressBar _progress = new() { Height = 5, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    readonly DataGrid _credentials = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        SelectionMode = DataGridSelectionMode.Extended,
        SelectionUnit = DataGridSelectionUnit.FullRow,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
    };
    readonly ObservableCollection<CredentialTargetRow> _credentialRows = [];
    bool _running;

    public BulkAccountIdentityWindow(IReadOnlyList<ClientInfo> clients, HostSettings settings, Window owner)
    {
        _clients = clients;
        _settings = settings;
        Owner = owner;
        Title = $"Signed-in accounts inventory — {clients.Count} computers";
        Width = 1220;
        Height = 680;
        MinWidth = 820;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AddColumn("Computer", nameof(ConnectedAccountInventory.Computer), 150);
        AddColumn("State", nameof(ConnectedAccountInventory.State), 125);
        AddColumn("Signed-in user", nameof(ConnectedAccountInventory.User), 145);
        AddColumn("Windows identity", nameof(ConnectedAccountInventory.Identity), 220);
        AddColumn("Microsoft account", nameof(ConnectedAccountInventory.MicrosoftAccountText), 125);
        AddColumn("Stored app/account targets", nameof(ConnectedAccountInventory.CredentialTargetsText), 330, true);
        AddColumn("Details", nameof(ConnectedAccountInventory.Error), 260, true);

        _credentials.Columns.Add(new DataGridTextColumn { Header = "Computer", Width = 160, Binding = new Binding(nameof(CredentialTargetRow.Computer)) });
        _credentials.Columns.Add(new DataGridTextColumn { Header = "Signed-in user", Width = 170, Binding = new Binding(nameof(CredentialTargetRow.User)) });
        _credentials.Columns.Add(new DataGridTextColumn { Header = "Stored app/account target", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding(nameof(CredentialTargetRow.Target)) });
        _credentials.Columns.Add(new DataGridTextColumn { Header = "Removal result", Width = 220, Binding = new Binding(nameof(CredentialTargetRow.Result)) });
        _credentials.ItemsSource = _credentialRows;
        _credentials.SelectionChanged += (_, _) => UpdateActionState();

        _refresh = new Button { Content = "_Recheck", Tag = "↻", ToolTip = "Reload signed-in account information from every selected computer." };
        _refresh.Click += async (_, _) => await LoadAsync();
        _remove = new Button
        {
            Content = "_Remove selected stored credentials...",
            Tag = "",
            ToolTip = "Remove only the selected computer and Credential Manager target pairs.",
            Margin = new Thickness(6, 0, 0, 0),
            IsEnabled = false,
        };
        _remove.SetResourceReference(StyleProperty, "DestructiveActionButtonStyle");
        _remove.Click += async (_, _) => await RemoveSelectedAsync();
        var close = new Button { Content = "_Close", Tag = "✕", IsCancel = true };
        close.Click += (_, _) => Close();
        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        controls.Children.Add(_refresh);
        controls.Children.Add(_remove);
        var note = Ui.Explanation(
            "This inventory shows the active Windows identity and Windows Credential Manager targets reported by each signed-in user's helper. " +
            "It never reads passwords, and Credential Manager is not a complete list of accounts inside every application. Local Windows users remain in Local accounts. " +
            "To sign apps out, select exact computer/target rows in the lower table and review the scope before removal.",
            new Thickness(0, 0, 0, 8));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
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
        var results = new Grid();
        results.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        results.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        results.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.8, GridUnitType.Star) });
        results.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        results.Children.Add(_grid);
        var credentialHeading = new TextBlock
        {
            Text = "SELECTABLE STORED CREDENTIAL TARGETS",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 4),
        };
        Grid.SetRow(credentialHeading, 1);
        results.Children.Add(credentialHeading);
        Grid.SetRow(_credentials, 2);
        results.Children.Add(_credentials);
        Grid.SetRow(_progress, 3);
        results.Children.Add(_progress);
        root.Children.Add(results);
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
        Ui.RefreshOnF5(this, LoadAsync);
    }

    void AddColumn(string header, string path, double width, bool wrap = false)
    {
        var column = new DataGridTextColumn { Header = header, Width = width, Binding = new Binding(path) };
        if (wrap)
        {
            column.ElementStyle = new Style(typeof(TextBlock));
            column.ElementStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            column.ElementStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2, 4, 2, 4)));
        }
        _grid.Columns.Add(column);
    }

    async Task LoadAsync()
    {
        if (_running) return;
        _running = true;
        _refresh.IsEnabled = false;
        try
        {
            var completed = 0;
            _status.Text = $"Checking signed-in accounts on {_clients.Count} computers...";
            using var gate = new SemaphoreSlim(8);
            var results = await Task.WhenAll(_clients.Select(async client =>
            {
                await gate.WaitAsync();
                try { return await LoadOneAsync(client); }
                finally
                {
                    gate.Release();
                    var count = Interlocked.Increment(ref completed);
                    await Dispatcher.InvokeAsync(() => _status.Text = $"Checked {count} of {_clients.Count} computers...");
                }
            }));
            var ordered = results.OrderBy(result => result.Inventory.Computer, StringComparer.OrdinalIgnoreCase).ToList();
            _grid.ItemsSource = ordered.Select(result => result.Inventory).ToList();
            _credentialRows.Clear();
            foreach (var result in ordered)
            foreach (var target in result.Inventory.CredentialTargets)
                _credentialRows.Add(new CredentialTargetRow(result.Client, result.Inventory.User, target));
            var available = ordered.Count(result => result.Inventory.State == "Available");
            var noUser = ordered.Count(result => result.Inventory.State == "No signed-in user");
            _status.Text = $"Available: {available} of {_clients.Count}. No signed-in user: {noUser}. Other unavailable: {_clients.Count - available - noUser}.";
        }
        finally
        {
            _running = false;
            _refresh.IsEnabled = true;
            UpdateActionState();
        }
    }

    async Task<AccountIdentityLoadResult> LoadOneAsync(ClientInfo client)
    {
        if (!client.SupportsCommand(Cmd.AccountIdentityList))
            return new(client, ConnectedAccountInventory.Unavailable(client.Name,
                $"Client protocol {Protocol.RequiredVersion(Cmd.AccountIdentityList)} or advertised capability required.", true));
        if (client.ExclusiveOperation.Length > 0)
            return new(client, ConnectedAccountInventory.Unavailable(client.Name, client.ExclusiveOperation));
        try
        {
            using var session = await ClientSession.OpenAsync(client, _settings);
            var data = await session.CallAtAsync(Levels.User, Cmd.AccountIdentityList);
            return new(client, ConnectedAccountInventory.FromResponse(client.Name, data));
        }
        catch (Exception ex)
        {
            return new(client, ConnectedAccountInventory.Unavailable(client.Name, ex.Message));
        }
    }

    void UpdateActionState() => _remove.IsEnabled = !_running &&
        _credentials.SelectedItems.Cast<CredentialTargetRow>().Any(row => row.Result != "Removed");

    async Task RemoveSelectedAsync()
    {
        if (_running) return;
        var selected = _credentials.SelectedItems.Cast<CredentialTargetRow>()
            .Where(row => row.Result != "Removed")
            .DistinctBy(row => $"{row.Client.ClientId}\0{row.Client.Address}\0{row.Client.Name}\0{row.Target}",
                StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select one or more computer/credential-target rows first.", "Remove stored credentials");
            return;
        }

        var computers = selected.Select(row => row.Client).Distinct().ToList();
        var targetText = OperationTargetSummary.Format(computers);
        var exactPairs = string.Join("\n", selected.Select(row => $"• {row.Computer} — {row.Target}"));
        var review = new BulkOperationReview(
            "Review stored credential removal",
            targetText.Heading + "\n" + targetText.Detail,
            [
                new("Exact computer / credential target pairs", exactPairs),
                new("Windows users and profiles", "Not changed"),
                new("Microsoft-account attachment", "Not changed; use Windows Account Settings on one computer"),
            ],
            "Per-pair results remain in this window. Use Recheck after reviewing the results to reload current Credential Manager targets.",
            "Removing a stored credential may sign the active user out of the associated app. This cannot restore the credential automatically.",
            "Remove credentials");
        if (new BulkOperationReviewWindow(this, review).Ask() != BulkOperationReviewDecision.Run) return;

        _running = true;
        _refresh.IsEnabled = false;
        _remove.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _progress.Minimum = 0;
        _progress.Maximum = selected.Count;
        _progress.Value = 0;
        var completed = 0;
        try
        {
            _status.Text = $"Removing 0 of {selected.Count} selected stored credentials...";
            using var gate = new SemaphoreSlim(8);
            await Task.WhenAll(selected.GroupBy(row => row.Client).Select(async group =>
            {
                await gate.WaitAsync();
                try
                {
                    ClientSession? session = null;
                    try
                    {
                        session = await ClientSession.OpenAsync(group.Key, _settings);
                        foreach (var row in group)
                        {
                            try
                            {
                                await session.CallAtAsync(Levels.User, Cmd.AccountIdentityRemove,
                                    new System.Text.Json.Nodes.JsonObject { ["target"] = row.Target });
                                row.Result = "Removed";
                            }
                            catch (Exception ex) { row.Result = "Failed: " + ex.Message; }
                            var count = Interlocked.Increment(ref completed);
                            await Dispatcher.InvokeAsync(() =>
                            {
                                _progress.Value = count;
                                _status.Text = $"Removing {count} of {selected.Count} selected stored credentials...";
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        foreach (var row in group)
                        {
                            row.Result = "Failed: " + ex.Message;
                            var count = Interlocked.Increment(ref completed);
                            await Dispatcher.InvokeAsync(() =>
                            {
                                _progress.Value = count;
                                _status.Text = $"Removing {count} of {selected.Count} selected stored credentials...";
                            });
                        }
                    }
                    finally { session?.Dispose(); }
                }
                finally { gate.Release(); }
            }));
            var removed = selected.Count(row => row.Result == "Removed");
            _status.Text = $"Removal finished: {removed} removed, {selected.Count - removed} failed. Review each pair below; Recheck reloads current targets.";
        }
        finally
        {
            _running = false;
            _refresh.IsEnabled = true;
            UpdateActionState();
        }
    }

    sealed record AccountIdentityLoadResult(ClientInfo Client, ConnectedAccountInventory Inventory);

    sealed class CredentialTargetRow(ClientInfo client, string user, string target) : INotifyPropertyChanged
    {
        string _result = "Ready";
        public ClientInfo Client { get; } = client;
        public string Computer => Client.Name;
        public string User { get; } = user;
        public string Target { get; } = target;
        public string Result
        {
            get => _result;
            set
            {
                if (_result == value) return;
                _result = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Result)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
