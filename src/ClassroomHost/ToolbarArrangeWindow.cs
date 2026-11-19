// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;

namespace ClassroomManager.Host;

/// <summary>One tab of the main toolbar as the program lays it out.</summary>
public sealed record ToolbarTabDefaults(string Tab, IReadOnlyList<string> Top, IReadOnlyList<string> More);

/// <summary>Moves the actions of a toolbar tab between the top row, the "More" part and hidden, and orders them.</summary>
public sealed class ToolbarArrangeWindow : Window
{
    readonly HostSettings _settings;
    readonly IReadOnlyList<ToolbarTabDefaults> _tabs;
    readonly Action _changed;
    readonly Dictionary<string, ToolbarLayout> _layouts;
    readonly ComboBox _tab = new() { Width = 220 };
    readonly ListBox _top = new() { MinHeight = 220 };
    readonly ListBox _more = new() { MinHeight = 220 };
    readonly ListBox _hidden = new() { MinHeight = 220 };

    sealed record Entry(string Id)
    {
        public override string ToString() => ToolbarArrangement.Label(Id);
    }

    public ToolbarArrangeWindow(HostSettings settings, Window? owner, IReadOnlyList<ToolbarTabDefaults>? tabs = null, Action? changed = null)
    {
        _settings = settings;
        _tabs = tabs ?? [];
        _changed = changed ?? (() => { });
        _layouts = _tabs.ToDictionary(tab => tab.Tab,
            tab => ToolbarArrangement.Resolve(tab.Top, tab.More, settings.ToolbarLayouts.GetValueOrDefault(tab.Tab)));
        Owner = owner;
        Title = "Arrange toolbar";
        Width = 860;
        Height = 560;
        MinWidth = 700;
        MinHeight = 420;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;

        _tab.ItemsSource = _tabs.Select(tab => tab.Tab).ToList();
        _tab.SelectionChanged += (_, _) => Show();
        foreach (var list in new[] { _top, _more, _hidden })
            list.GotFocus += (_, _) => { foreach (var other in new[] { _top, _more, _hidden }) if (other != list) other.SelectedItem = null; };

        var columns = new Grid();
        for (int i = 0; i < 3; i++) columns.ColumnDefinitions.Add(new ColumnDefinition());
        void Column(int index, string label, ListBox list)
        {
            var panel = new DockPanel { Margin = new Thickness(index == 0 ? 0 : 6, 0, index == 2 ? 0 : 6, 0) };
            var title = Ui.Label(label, list);
            DockPanel.SetDock(title, Dock.Top);
            panel.Children.Add(title);
            panel.Children.Add(list);
            Grid.SetColumn(panel, index);
            columns.Children.Add(panel);
        }
        Column(0, "_Top row:", _top);
        Column(1, "In \"_More\":", _more);
        Column(2, "_Hidden:", _hidden);

        var up = new Button { Content = "Move _up", Tag = FluentIcons.Up, ToolTip = "Move up — show the selected action earlier in its row." };
        up.Click += (_, _) => Move(-1);
        var down = new Button { Content = "Move _down", Tag = FluentIcons.Down, ToolTip = "Move down — show the selected action later in its row." };
        down.Click += (_, _) => Move(1);
        void UpdateMoveButtons()
        {
            up.IsEnabled = _top.SelectedIndex > 0;
            down.IsEnabled = _top.SelectedIndex >= 0 && _top.SelectedIndex < _top.Items.Count - 1;
        }
        foreach (var list in new[] { _top, _more, _hidden }) list.SelectionChanged += (_, _) => UpdateMoveButtons();
        UpdateMoveButtons();
        var toTop = new Button { Content = "To t_op row", Tag = FluentIcons.Up, ToolTip = "To top row — show the selected action in the tab's top row." };
        toTop.Click += (_, _) => MoveTo(layout => layout.Top);
        var toMore = new Button { Content = "To \"Mo_re\"", Tag = FluentIcons.Down, ToolTip = "To \"More\" — show the selected action under the tab's \"More\" part." };
        toMore.Click += (_, _) => MoveTo(layout => layout.More);
        var hide = new Button { Content = "H_ide", Tag = FluentIcons.Clear, ToolTip = "Hide — do not show the selected action in the toolbar." };
        hide.Click += (_, _) =>
        {
            if (Selected() is var (_, id) && ToolbarArrangement.NeverHidden.Contains(id))
            {
                MessageBox.Show(this, $"\"{ToolbarArrangement.Label(id)}\" stays in the toolbar, so this window can always be opened again. Move it to the top row or to \"More\" instead.",
                    "Arrange toolbar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MoveTo(layout => layout.Hidden);
        };
        var reset = new Button { Content = "Reset this _tab", Tag = FluentIcons.Refresh, ToolTip = "Reset this tab — put the tab back as Classroom Manager lays it out." };
        reset.Click += (_, _) => ResetTab();
        var save = new Button { Content = "_Save", Tag = FluentIcons.Save, IsDefault = true };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "_Cancel", Tag = FluentIcons.Cancel, IsCancel = true };

        var top = Ui.Spaced(new WrapPanel { Margin = new Thickness(0, 0, 0, 8), Children = { Ui.Label("T_ab:", _tab), _tab } });
        var buttons = Ui.Spaced(new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { up, down, toTop, toMore, hide, reset, save, cancel } });
        var note = Ui.Explanation(
            "Select a top-row action and use Move up or Move down to change its order. More actions are always alphabetical. " +
            "Teacher mode still decides which actions exist: an action it hides stays hidden. " +
            "Arrange toolbar and Interface settings cannot be hidden. If the toolbar is ever lost, press Ctrl+Shift+T or run \"ClassroomCli toolbar-reset\". " +
            "The arrangement is saved for your Windows account and goes with Export teacher settings.",
            new Thickness(0, 0, 0, 8));

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(note);
        root.Children.Add(buttons);
        root.Children.Add(columns);
        Content = root;
        if (_tabs.Count > 0) _tab.SelectedIndex = 0;
        ModuleUiPolicy.Watch(this, settings, () => Show());
    }

    ToolbarLayout? Current => _tab.SelectedItem is string tab ? _layouts.GetValueOrDefault(tab) : null;

    void Show(string? select = null)
    {
        if (Current is not { } layout) return;
        foreach (var (list, ids) in new[] { (_top, layout.Top), (_more, layout.More), (_hidden, layout.Hidden) })
        {
            list.ItemsSource = (ReferenceEquals(list, _more) ? ToolbarArrangement.OrderMore(ids, ClassroomManager.Shared.Loc.T) : ids)
                .Where(id => ModuleUiPolicy.ToolbarEntryVisible(_settings, id)).Select(id => new Entry(id)).ToList();
            if (select is not null && ((List<Entry>)list.ItemsSource).FirstOrDefault(entry => entry.Id == select) is { } entry)
            { list.SelectedItem = entry; list.Focus(); }
        }
    }

    (List<string> Row, string Id)? Selected()
    {
        if (Current is not { } layout) return null;
        foreach (var (list, row) in new[] { (_top, layout.Top), (_more, layout.More), (_hidden, layout.Hidden) })
            if (list.SelectedItem is Entry entry) return (row, entry.Id);
        return null;
    }

    void Move(int step)
    {
        if (Selected() is not var (row, id)) return;
        if (!ReferenceEquals(row, Current?.Top)) return;
        var index = row.IndexOf(id);
        if (index + step < 0 || index + step >= row.Count) return;
        row.RemoveAt(index);
        row.Insert(index + step, id);
        Show(id);
    }

    void MoveTo(Func<ToolbarLayout, List<string>> target)
    {
        if (Current is not { } layout || Selected() is not var (row, id)) return;
        var to = target(layout);
        if (ReferenceEquals(to, row)) return;
        row.Remove(id);
        to.Add(id);
        Show(id);
    }

    void ResetTab()
    {
        if (_tab.SelectedItem is not string name) return;
        var tab = _tabs.First(item => item.Tab == name);
        _layouts[name] = ToolbarArrangement.Resolve(tab.Top, tab.More, null);
        Show();
    }

    void Save()
    {
        foreach (var tab in _tabs)
        {
            var layout = _layouts[tab.Tab];
            var unchanged = layout.Hidden.Count == 0 && layout.Top.SequenceEqual(tab.Top) && layout.More.SequenceEqual(tab.More);
            // A tab left as the program lays it out is not stored, so later versions can still change its defaults.
            if (unchanged) _settings.ToolbarLayouts.Remove(tab.Tab);
            else _settings.ToolbarLayouts[tab.Tab] = layout.Copy();
        }
        _settings.Save();
        _changed();
        DialogResult = true;
    }
}
