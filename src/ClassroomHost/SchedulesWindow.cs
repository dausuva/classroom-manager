// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class ScheduleRow
{
    public ScheduledAction Item { get; init; } = new();
    public string EnabledText => ModuleLanguages.Automation.Text(Item.Enabled ? "on" : "off");
    /// <summary>Only the known sample is product wording; teacher-provided names remain data.</summary>
    public string Name => Item.Name == "Sync the computers' time" ? ModuleLanguages.Automation.Text("Sync the computers' time") : Item.Name;
    public string Time => Item.Time;
    public string Days => (Item.Monthly ? ModuleLanguages.Automation.Text("1st ") : "") + string.Join(",", Item.Days.Select(ModuleLanguages.Automation.Text));
    public string ActionText => SchedulesWindow.Translated(Item.Action, SchedulesWindow.ActionLabel(Item.Action));
    /// <summary>For a catch-up schedule, how many computers had its latest run; otherwise the day it last ran.</summary>
    public string Progress { get; init; } = "";
    public string Target => Item.AllComputers ? ModuleLanguages.Automation.Text("All computers") : Item.Room.Length > 0
        ? ModuleLanguages.Automation.Format($"Room: {Item.Room}") : ModuleLanguages.Automation.Format($"{Item.Addresses.Count} computer(s)");
}

/// <summary>Add, enable/disable and delete scheduled actions. They run while the Host is open, and while it is closed when RunSchedulesWhenClosed is on.</summary>
public sealed class SchedulesWindow : Window
{
    static ScopedTranslation Catalog => ModuleLanguages.Automation;
    static readonly (string Code, string Label)[] ActionsBeforeModules =
    {
        ("lock", "Lock screen (with message)"),
        ("unlock", "Unlock screen"),
        ("message", "Show message pop-up"),
        ("wake", "Wake up (Wake-on-LAN)"),
        ("timesync", "Sync time to this computer"),
    };
    static readonly (string Code, string Label)[] ActionsAfterModules =
    {
        ("shutdown", "Shut down"),
        ("restart", "Restart"),
        ("sleep", "Sleep"),
        ("command", "Run command (cmd, elevated)"),
    };

    /// <summary>The Host's own actions with the ones the modules add between them. A module cannot replace an action the Host has.</summary>
    static IReadOnlyList<(string Code, string Label)> Actions
    {
        get
        {
            var own = ActionsBeforeModules.Concat(ActionsAfterModules).Select(action => action.Code).ToHashSet();
            return ActionsBeforeModules.Concat(BuiltInModules.ModuleActions.Where(action => !own.Contains(action.Code))).Concat(ActionsAfterModules).ToArray();
        }
    }
    static readonly string[] DayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    /// <summary>An action's text in the current language. A module's label is translated by the Host's own translation file.</summary>
    internal static string Translated(string code, string label) =>
        BuiltInModules.ActionOwner(code) is not null ? Loc.T(label) : Catalog.Text(label);

    public static string ActionLabel(string code) => Actions.FirstOrDefault(a => a.Code == code).Label ?? code;

    readonly HostSettings _settings;
    readonly Func<List<string>> _selectedAddresses;
    readonly Func<List<string>> _rooms;
    readonly ListView _list = new();

    public SchedulesWindow(HostSettings settings, Window owner, Func<List<string>> selectedAddresses, Func<List<string>> rooms)
    {
        _settings = settings;
        _selectedAddresses = selectedAddresses;
        _rooms = rooms;
        Owner = owner;
        Title = Catalog.Text("Scheduled actions");
        UiTranslator.SetPreserveText(this, true);
        Width = 1120;   // every column, the latest run included, without scrolling sideways
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new GridView();
        grid.Columns.Add(Col("On", "EnabledText", 70));
        grid.Columns.Add(Col("Name", "Name", 200));
        grid.Columns.Add(Col("Time", "Time", 60));
        grid.Columns.Add(Col("Days", "Days", 190));
        grid.Columns.Add(Col("Action", "ActionText", 200));
        grid.Columns.Add(Col("Target", "Target", 120));
        grid.Columns.Add(Col("Latest run", "Progress", 230));
        _list.View = grid;

        var add = new Button { Content = Catalog.Text("_Add...") };
        var toggle = new Button { Content = Catalog.Text("_Enable / disable") };
        var delete = new Button { Content = Catalog.Text("_Delete") };
        add.Click += (_, _) => Add();
        toggle.Click += (_, _) => Toggle();
        delete.Click += (_, _) => Delete();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        buttons.Children.Add(add);
        buttons.Children.Add(toggle);
        buttons.Children.Add(delete);

        var note = Ui.Explanation(Catalog.Text(
            "Schedules run once a day at the chosen time while this Host is open, and also while it is closed when Host settings say so " +
            "(a time missed by up to 10 minutes still runs). Times are this computer's local time. " +
            "\"Sync the computers' time\" is a sample: enable it to set every computer's clock to this PC's each day."),
            new Thickness(0, 8, 0, 0));
        foreach (var button in ((DockPanel)note).Children.OfType<Button>())
            System.Windows.Automation.AutomationProperties.SetName(button, Loc.T("About this window"));

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Top);
        DockPanel.SetDock(note, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(note);
        root.Children.Add(_list);
        Content = root;
        Reload();
        ModuleUiPolicy.WatchVisibility(this, settings, BuiltInModules.Automation, add, toggle);
    }

    static GridViewColumn Col(string header, string path, double width) =>
        new() { Header = Catalog.Text(header), Width = width, DisplayMemberBinding = new Binding(path) };

    void Reload()
    {
        var lastRuns = ScheduleRuns.LastRuns();
        _list.ItemsSource = _settings.Schedules.Select(s => new ScheduleRow { Item = s, Progress = Progress(s, lastRuns) }).ToList();
    }

    string Progress(ScheduledAction schedule, IReadOnlyDictionary<string, string> lastRuns)
    {
        if (!ScheduleRuns.CatchesUp(schedule))
            return lastRuns.TryGetValue(schedule.Id, out var day) ? Catalog.Format($"Ran on {day}") : "";
        if (ScheduleRuns.LatestOccurrence(schedule, DateTime.Now) is not { } at) return "";
        var keys = ScheduleRuns.Targets(_settings.Computers, schedule).Select(ScheduleRuns.ComputerKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var done = ScheduleRuns.DoneCount(schedule.Id, ScheduleRuns.OccurrenceKey(at), keys);
        var unknown = ScheduleRuns.UnknownCount(schedule.Id, ScheduleRuns.OccurrenceKey(at), keys);
        return Catalog.Format($"{done} of {keys.Count} computer(s) since {at:yyyy-MM-dd HH:mm}") +
            (unknown > 0 ? Catalog.Format($"; {unknown} outcome(s) unknown: review before retrying") : "");
    }

    void Add()
    {
        if (!BuiltInModules.Enabled(_settings, BuiltInModules.Automation)) return;
        var selected = _selectedAddresses();
        const string all = "All computers";
        var targets = new List<(string Value, string Shown)> { ("selected", Catalog.Format($"Selected computers ({selected.Count})")), (all, Catalog.Text(all)) };
        targets.AddRange(_rooms().Select(r => ("Room: " + r, Catalog.Format($"Room: {r}"))));

        var dlg = new FormDialog(Catalog.Text("New scheduled action"), this);
        UiTranslator.SetPreserveText(dlg, true);
        dlg.Text("name", Catalog.Text("Name"), Catalog.Text("Morning lock"))
            .DisplayCombo("action", Catalog.Text("Action"), Actions.Where(a => ModuleUiPolicy.ScheduleActionVisible(_settings, a.Code))
                .Select(a => (a.Code, Translated(a.Code, a.Label))).ToArray())
            .Text("time", Catalog.Text("Time (HH:mm, 24-hour)"), "08:00")
            .Check("d0", Catalog.Text("Mon"), true).Check("d1", Catalog.Text("Tue"), true).Check("d2", Catalog.Text("Wed"), true).Check("d3", Catalog.Text("Thu"), true)
            .Check("d4", Catalog.Text("Fri"), true).Check("d5", Catalog.Text("Sat")).Check("d6", Catalog.Text("Sun"))
            .DisplayCombo("target", Catalog.Text("Applies to"), targets)
            .Text("title", Catalog.Text("Title (lock / message)"), Catalog.Text("Notice"))
            .Text("text", Catalog.Text("Message text, or command line for \"Run command\""), Catalog.Text("Please look at the teacher."))
            .Check("monthly", Catalog.Text("Monthly: only the first of the ticked weekdays in each month"))
            .Check("catchUp", Catalog.Text("Catch up: computers that are off at that time get it as soon as they are online, until the next time (not for lock, message, wake or power)"));
        if (!dlg.Ask("Add")) return;

        if (!TimeSpan.TryParse(dlg.S("time"), out var tod) || tod < TimeSpan.Zero || tod >= TimeSpan.FromDays(1))
        {
            MessageBox.Show(this, Catalog.Text("Enter the time as HH:mm, for example 08:30."));
            return;
        }

        var target = dlg.S("target");
        var item = new ScheduledAction
        {
            Name = dlg.S("name").Length > 0 ? dlg.S("name") : Catalog.Text("Schedule"),
            Action = dlg.S("action"),
            Time = $"{(int)tod.TotalHours:00}:{tod.Minutes:00}",
            Days = DayNames.Where((_, i) => dlg.B("d" + i)).ToList(),
            Title = dlg.S("title"),
            Text = dlg.S("text"),
            CatchUp = dlg.B("catchUp"),
            Monthly = dlg.B("monthly"),
        };
        if (ScheduleRuns.AlwaysCatchUp.Contains(item.Action)) item.CatchUp = true;
        if (item.CatchUp && !ScheduleRuns.CatchUpActions.Contains(item.Action))
        {
            // A lock, message or shutdown hours late would surprise a class; only actions harmless at any hour catch up.
            MessageBox.Show(this, Catalog.Text("Lock, message, wake and power do not catch up: hours late they would surprise a class. Untick Catch up for this action."));
            return;
        }
        if (target.StartsWith("Room: ")) item.Room = target["Room: ".Length..];
        else if (target == all) item.AllComputers = true;
        else item.Addresses = selected;

        if (item.Days.Count == 0) { MessageBox.Show(this, Catalog.Text("Pick at least one day.")); return; }
        if (!item.AllComputers && item.Room.Length == 0 && item.Addresses.Count == 0)
        {
            MessageBox.Show(this, Catalog.Text("Select computers in the main window first, or pick a room."));
            return;
        }

        _settings.Schedules.Add(item);
        _settings.Save();
        Reload();
    }

    ScheduleRow? Current() => _list.SelectedItem as ScheduleRow;

    void Toggle()
    {
        if (!BuiltInModules.Enabled(_settings, BuiltInModules.Automation)) return;
        if (Current() is not { } row) return;
        row.Item.Enabled = !row.Item.Enabled;
        _settings.Save();
        Reload();
    }

    void Delete()
    {
        if (Current() is not { } row) return;
        _settings.Schedules.Remove(row.Item);
        _settings.Save();
        Reload();
    }
}
