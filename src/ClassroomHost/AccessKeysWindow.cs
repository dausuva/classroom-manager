// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>One access key as reported by a client's <c>key.access.list</c>; never contains the key itself.</summary>
public sealed record AccessKeyInfo(string Name, string Role, string Fingerprint, bool Primary, string Ends = "", bool Expired = false, string Rooms = "");

/// <summary>An access key name across the selected computers.</summary>
public sealed class AccessKeyRow
{
    public string Name { get; init; } = "";
    public string Role { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string Computers { get; init; } = "";
    public string Notes { get; init; } = "";
    public bool Primary { get; init; }
    public IReadOnlyList<ClientInfo> Clients { get; init; } = [];
}

/// <summary>What this Host's keys amount to across the computers it has reached.</summary>
public static class KeyRoleSummary
{
    /// <summary>
    /// True when every computer that reported a role granted only Teacher. One Admin answer, or no
    /// answer at all (nothing checked yet, or clients too old to report roles), means administrator
    /// actions stay visible: hiding them wrongly would lock an administrator out of their own tools.
    /// </summary>
    public static bool IsTeacherOnly(IEnumerable<string> reportedRoles)
    {
        var known = reportedRoles.Where(role => role.Length > 0).ToList();
        return known.Count > 0 && known.All(role => !role.Equals(nameof(OperatorRole.Admin), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What to tell someone who switches the Host to Admin mode: empty when an Admin key is in use or
    /// nothing is known yet, otherwise that the administrator actions now shown will be refused.
    /// </summary>
    public static string AdminModeWarning(IEnumerable<string> reportedRoles) => IsTeacherOnly(reportedRoles)
        ? "No Admin key is set in this Classroom Manager: every computer it has reached accepted its key as a Teacher key.\n\n" +
          "The administrator actions are shown now, but the computers will refuse them. " +
          "An administrator adds an Admin key under Settings > Lab keys (or Host settings)."
        : "";
}

public static class AccessKeyInventory
{
    public static List<AccessKeyInfo> FromJson(JsonObject data) =>
        (data["keys"] as JsonArray ?? []).OfType<JsonObject>().Select(item => new AccessKeyInfo(
            item["name"]?.GetValue<string>() ?? "",
            item["role"]?.GetValue<string>() ?? "",
            item["fingerprint"]?.GetValue<string>() ?? "",
            item["primary"]?.GetValue<bool>() ?? false,
            item["expiresUtc"]?.GetValue<string>() ?? "",
            item["expired"]?.GetValue<bool>() ?? false,
            string.Join(", ", (item["rooms"] as JsonArray ?? []).Select(room => room?.GetValue<string>() ?? "").Where(room => room.Length > 0)))).ToList();

    /// <summary>
    /// The end of a key the administrator typed as a date, as the UTC time the Client is given: the end of that day on this computer.
    /// Empty means the key does not end. A date that is not a date, or is already past, is refused with the reason.
    /// </summary>
    public static bool TryEndOfDay(string text, DateTime now, out string utc, out string reason)
    {
        utc = "";
        reason = "";
        if (text.Trim().Length == 0) return true;
        if (!DateTime.TryParse(text.Trim(), System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out var day))
        {
            reason = "The end date is not a date. Use a date such as 2026-12-18, or leave it empty for a key that does not end.";
            return false;
        }
        var end = day.Date.AddDays(1);                                    // the key works through the whole of that day
        if (end <= now) { reason = "That date is already past."; return false; }
        utc = end.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        return true;
    }

    /// <summary>
    /// One row per key name. A name whose role or secret differs between computers is flagged, because
    /// the person holding that key then has different rights, or no access, depending on the computer.
    /// </summary>
    public static List<AccessKeyRow> Merge(IReadOnlyDictionary<ClientInfo, IReadOnlyList<AccessKeyInfo>> inventories)
    {
        var total = inventories.Count;
        return inventories
            .SelectMany(pair => pair.Value.Select(key => (Client: pair.Key, Key: key)))
            .GroupBy(entry => entry.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var roles = group.Select(entry => entry.Key.Role).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var fingerprints = group.Select(entry => entry.Key.Fingerprint).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var clients = group.Select(entry => entry.Client).Distinct().ToList();
                var notes = new List<string>();
                if (group.Any(entry => entry.Key.Primary)) notes.Add("The lab's original key; change it with Rotate shared key");
                if (roles.Count > 1) notes.Add("Role differs between computers");
                if (fingerprints.Count > 1) notes.Add("Different keys share this name");
                var ends = group.Select(entry => entry.Key.Ends).Where(end => end.Length > 0).Distinct().ToList();
                if (ends.Count == 1)
                {
                    var when = ends[0].Replace('T', ' ').TrimEnd('Z');
                    notes.Add(group.Any(entry => entry.Key.Expired) ? $"Ended {when} UTC" : $"Ends {when} UTC");
                }
                else if (ends.Count > 1) notes.Add("End differs between computers");
                if (group.Any(entry => entry.Key.Ends.Length == 0) && ends.Count > 0) notes.Add("No end on some computers");
                var rooms = group.Select(entry => entry.Key.Rooms).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (rooms.Count == 1 && rooms[0].Length > 0) notes.Add($"Only in rooms: {rooms[0]}");
                else if (rooms.Count > 1) notes.Add("Rooms differ between computers");
                if (clients.Count < total) notes.Add($"Missing on {total - clients.Count}");
                return new AccessKeyRow
                {
                    Name = group.First().Key.Name,
                    Role = string.Join(" / ", roles),
                    Fingerprint = fingerprints.Count == 1 ? fingerprints[0] : "(differs)",
                    Computers = $"{clients.Count} of {total}",
                    Notes = string.Join("; ", notes),
                    Primary = group.Any(entry => entry.Key.Primary),
                    Clients = clients,
                };
            })
            .OrderByDescending(row => row.Primary)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// Shows which named keys the selected computers accept and lets an administrator add a key with a
/// role (for example one Teacher key per member of staff) or revoke one. The clients enforce the
/// roles; this window only manages the list.
/// </summary>
public sealed class AccessKeysWindow : Window
{
    readonly IReadOnlyList<ClientInfo> _clients;
    readonly HostSettings _settings;
    readonly BulkOperationRunner _runOperation;
    readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        SelectionMode = DataGridSelectionMode.Single,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
    };
    readonly TextBlock _status = Ui.StatusText();
    readonly Button _add;
    readonly Button _remove;
    readonly Button _refresh;
    bool _running;

    public AccessKeysWindow(IReadOnlyList<ClientInfo> clients, HostSettings settings, Window owner, BulkOperationRunner runOperation)
    {
        _clients = clients;
        _settings = settings;
        _runOperation = runOperation;
        Owner = owner;
        Title = $"Access keys - {clients.Count} computer(s)";
        Width = 900;
        Height = 520;
        MinWidth = 640;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        AddColumn("Key name", nameof(AccessKeyRow.Name), 190);
        AddColumn("Role", nameof(AccessKeyRow.Role), 90);
        AddColumn("Fingerprint", nameof(AccessKeyRow.Fingerprint), 95);
        AddColumn("Computers", nameof(AccessKeyRow.Computers), 90);
        AddColumn("Notes", nameof(AccessKeyRow.Notes), 360);

        _refresh = ActionButton("_Recheck", "↻", "Reload the access keys from every selected computer.");
        _refresh.Click += async (_, _) => await LoadAsync();
        _add = ActionButton("_Add key...", FluentIcons.Key, "Add key — create a named Teacher or Admin key on the selected computers.");
        _add.Click += async (_, _) => await AddAsync();
        _remove = ActionButton("Re_move selected...", FluentIcons.Delete, "Remove — revoke the selected key on the computers that have it.");
        _remove.Click += async (_, _) => await RemoveAsync();
        var close = ActionButton("_Close", "✕", "Close this window.");
        close.IsCancel = true;
        close.Click += (_, _) => Close();

        var note = Ui.Explanation(
            "Each computer decides what a key may do. A Teacher key can lock, message, send and collect files, set wallpaper, " +
            "run Exam mode and power computers off; everything else needs an Admin key. Give each person their own key so it " +
            "can be revoked on its own and appears by name in the computer's log.",
            new Thickness(0, 0, 0, 8));
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { _refresh, _add, _remove, close }) actions.Children.Add(button);

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

    void AddColumn(string header, string path, double width) =>
        _grid.Columns.Add(new DataGridTextColumn { Header = header, Width = width, Binding = new Binding(path) });

    static Button ActionButton(string content, string icon, string tooltip) => new()
    {
        Content = content,
        Tag = icon,
        ToolTip = tooltip,
        MinWidth = 110,
        Margin = new Thickness(8, 0, 0, 0),
    };

    void SetRunning(bool value)
    {
        _running = value;
        _refresh.IsEnabled = _add.IsEnabled = _remove.IsEnabled = !value;
    }

    async Task LoadAsync()
    {
        if (_running) return;
        SetRunning(true);
        try
        {
            _status.Text = $"Reading access keys from {_clients.Count} computer(s)...";
            var results = await Task.WhenAll(_clients.Select(async client =>
            {
                try
                {
                    using var session = await ClientSession.OpenAsync(client, _settings);
                    var data = await session.CallAsync(Cmd.AccessKeysList);
                    return (Client: client, Keys: (IReadOnlyList<AccessKeyInfo>?)AccessKeyInventory.FromJson(data), Error: "");
                }
                catch (Exception ex) { return (Client: client, Keys: null, Error: ex.Message); }
            }));
            var inventories = results.Where(result => result.Keys is not null)
                .ToDictionary(result => result.Client, result => result.Keys!);
            TableSorting.SetItemsPreservingSort(_grid, AccessKeyInventory.Merge(inventories));
            var failures = results.Where(result => result.Keys is null).ToList();
            _status.Text = $"Read access keys from {inventories.Count} of {_clients.Count} computer(s)." +
                (failures.Count == 0 ? "" : " Unavailable: " + string.Join("; ", failures.Select(result => $"{result.Client.Name}: {result.Error}")));
        }
        finally { SetRunning(false); }
    }

    async Task AddAsync()
    {
        if (_running) return;
        var dlg = new FormDialog("Add access key", this)
            .Target(_clients)
            .Text("name", "Key _name (who holds it, for example “Room 12 teacher” or a person's name)")
            .Combo("role", "_Role", [nameof(OperatorRole.Teacher), nameof(OperatorRole.Admin), nameof(OperatorRole.Observer)])
            .Text("key", "_Key (generated; replace it only with another long random value)", SharedKey.Generate())
            .Text("ends", "_Ends on (a date such as 2026-12-18; empty = never)", "")
            .Text("rooms", "_Only in these rooms (names as assigned in the Host, separated by commas; empty = every room)", "")
            .Note("The key is shown once more after it has been added. Hand it to that person privately; they enter it under " +
                  "Settings > Lab keys > Add in the Classroom Host of their own Windows account. A key with an existing name is replaced.");
        if (!dlg.Ask("Add key")) return;

        var name = dlg.S("name");
        var role = dlg.S("role");
        var key = dlg.S("key");
        if (!AccessKeyInventory.TryEndOfDay(dlg.S("ends"), DateTime.Now, out var endsUtc, out var reason) ||
            !AccessKeyNames.IsAcceptable(name, out reason) || !SharedKey.IsAcceptable(key, out reason))
        {
            MessageBox.Show(this, reason, "Add access key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (role == nameof(OperatorRole.Admin) && MessageBox.Show(this,
                $"An Admin key gives '{name}' full control of the selected computers, the same as the lab's original key.\n\nAdd it?",
                "Add Admin key", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        var rooms = dlg.S("rooms");
        if (rooms.Length > 0)
        {
            // A key limited to rooms is checked against the room each computer was told it is in, so every target must be in a room.
            var unassigned = _clients.Where(client => client.Room.Trim().Length == 0).ToList();
            if (unassigned.Count > 0)
            {
                MessageBox.Show(this, "A key limited to rooms needs every selected computer to be assigned to a room first. Not assigned: " +
                    string.Join(", ", unassigned.Take(10).Select(client => client.Name)) + (unassigned.Count > 10 ? "..." : ""),
                    "Add access key", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        // A key added under this name again replaces a revoked one, so removals still waiting for that name must not remove it.
        if (KeyRevocations.Cancel(_settings, name) > 0) _settings.Save();
        var failed = await RunAsync($"Add {role} access key '{name}'", _clients.ToList(), async (client, session) =>
        {
            var args = new JsonObject { ["name"] = name, ["role"] = role, ["key"] = key };
            if (endsUtc.Length > 0) args["expiresUtc"] = endsUtc;
            if (rooms.Length > 0) { args["rooms"] = rooms; args["computerRoom"] = client.Room.Trim(); }
            var answer = await session.CallAsync(Cmd.AccessKeysSet, args);
            if (rooms.Length > 0 && !AccessKeyInventory.FromJson(answer).Any(info => info.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && info.Rooms.Length > 0))
            {
                // A Client older than this one ignores the rooms and would let the key in everywhere: take the key away again.
                await session.CallAsync(Cmd.AccessKeysRemove, new JsonObject { ["name"] = name });
                throw new InvalidOperationException("this computer ignores the rooms (its Client is too old); the key was removed again. Update the Client first.");
            }
            if (endsUtc.Length == 0) return;
            // A Client older than protocol 1.18 ignores the end and would keep the key for ever: say so, and take the key away again.
            if (!AccessKeyInventory.FromJson(answer).Any(info => info.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && info.Ends.Length > 0))
            {
                await session.CallAsync(Cmd.AccessKeysRemove, new JsonObject { ["name"] = name });
                throw new InvalidOperationException("this computer ignores the end date (its Client is older than protocol 1.18); the key was removed again. Update the Client first.");
            }
        });

        // Shown once: the Host does not store keys it hands out to other people.
        new AccessKeyHandoverWindow(this, name, role, key, _clients.Count - failed, _clients.Count).ShowDialog();
        await LoadAsync();
    }

    async Task RemoveAsync()
    {
        if (_running) return;
        if (_grid.SelectedItem is not AccessKeyRow row)
        {
            MessageBox.Show(this, "Select a key first.", "Remove access key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (row.Primary)
        {
            MessageBox.Show(this, "The lab's original key cannot be removed. Use Rotate shared key to replace it.",
                "Remove access key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"Remove the access key '{row.Name}' from {row.Clients.Count} computer(s)?\n\n" +
                "Whoever holds it can no longer connect to those computers. Connections that are open right now end when they close.",
                "Remove access key", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        // The computers that could not be read a moment ago may have the key too, so they are in the list; one that has no such key counts as done.
        var targets = _clients.ToList();
        var notDone = new List<ClientInfo>();
        await RunAsync($"Remove access key '{row.Name}'", targets, async (_, session) =>
        {
            try { await session.CallAsync(Cmd.AccessKeysRemove, new JsonObject { ["name"] = row.Name }); }
            catch (RemoteException ex) when (ex.Message.StartsWith("This computer has no access key named", StringComparison.Ordinal)) { }
        }, notDone);
        // A computer that is off still has the key. The Host finishes the removal when that computer next answers.
        if (KeyRevocations.Record(_settings, row.Name, notDone, DateTime.UtcNow) > 0)
        {
            _settings.Save();
            _status.Text += $" {notDone.Count} computer(s) did not answer; the Host removes the key there when they next answer.";
        }
        await LoadAsync();
    }

    /// <summary>Runs through the main window's Operations view so the change is logged per computer; returns the failure count.</summary>
    async Task<int> RunAsync(string title, List<ClientInfo> targets, Func<ClientInfo, ClientSession, Task> action, List<ClientInfo>? failedClients = null)
    {
        var failed = 0;
        SetRunning(true);
        try
        {
            _status.Text = title + "...";
            await _runOperation(targets, title, action, -1, "", (client, error) => { if (error.Length > 0) { failed++; failedClients?.Add(client); } });
            _status.Text = failed == 0 ? $"{title}: done on {targets.Count} computer(s)." : $"{title}: {failed} of {targets.Count} failed; see Operations in the main window.";
        }
        finally { SetRunning(false); }
        return failed;
    }
}

/// <summary>Shows a newly created key once, with a copy button, so it can be handed to its holder.</summary>
sealed class AccessKeyHandoverWindow : Window
{
    public AccessKeyHandoverWindow(Window owner, string name, string role, string key, int succeeded, int total)
    {
        Owner = owner;
        Title = "Hand over the access key";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var summary = new TextBlock
        {
            Text = succeeded == total
                ? $"The {role} key '{name}' was added to all {total} computer(s)."
                : $"The {role} key '{name}' was added to {succeeded} of {total} computer(s). See Operations for the ones that failed; adding it again later is safe.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var box = new TextBox
        {
            Text = key,
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 10),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Access key");
        var guidance = Ui.MutedText(
            "This is the only time the key is shown; this Host does not keep it. Give it to its holder privately. " +
            "They add it in the Classroom Host of their own Windows account under Settings > Lab keys > Add. " +
            "If it is lost, add a key with the same name again to replace it.",
            new Thickness(0, 0, 0, 12));
        var copy = new Button { Content = "_Copy key", Tag = FluentIcons.Page, MinWidth = 110, ToolTip = "Copy the key to the clipboard." };
        copy.Click += (_, _) => Clipboard.SetText(key);
        var close = new Button { Content = "_Done", MinWidth = 90, IsDefault = true, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(summary);
        panel.Children.Add(box);
        panel.Children.Add(guidance);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
