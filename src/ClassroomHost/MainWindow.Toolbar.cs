// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ClassroomManager.Shared;
#if BROWSER_INVENTORY_MODULE
using ClassroomManager.BrowserInventory;
#endif
#if REPORTING_MODULE
using ClassroomManager.Reporting;
#endif

namespace ClassroomManager.Host;

public partial class MainWindow
{
    static List<string> SplitList(string value, bool normalizeDomain) => value
        .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => normalizeDomain
            ? (item.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? item[4..] : item).TrimStart('.').ToLowerInvariant()
            : (item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? item : item + ".exe"))
        .Where(item => item.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    void BuildQuickActions()
    {
        // Keep these button instances stable: Arrange toolbar stores their identities and order.
        if (_builtInQuickButtons.Count == 0)
        foreach (var definition in QuickActionCatalog.All)
        {
            var button = new Button
            {
                Tag = definition.Icon,
                Content = definition.Label,
                CommandParameter = definition.Id,
                ToolTip = definition.Label,
            };
            AutomationProperties.SetName(button, definition.Label);
            CommandState.SetId(button, definition.CommandId);
            button.Click += QuickAction_Click;
            _builtInQuickButtons.Add(definition.Id, button);
            BuiltInQuickActionsPanel.Children.Add(button);
        }
        foreach (var (id, button) in _builtInQuickButtons)
            button.Visibility = _settings.QuickActions.Contains(id, StringComparer.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        while (QuickActionsPanel.Children.Count > 1) QuickActionsPanel.Children.RemoveAt(1);
        foreach (var custom in _settings.CustomQuickActions)
        {
            var button = new Button
            {
                Tag = CustomQuickActionIcon(custom.Kind),
                Content = custom.Name,
                CommandParameter = "custom:" + custom.Id,
                ToolTip = custom.Summary,
            };
            AutomationProperties.SetName(button, custom.Name);
            CommandState.SetId(button, "custom:" + custom.Id);
            button.Click += QuickAction_Click;
            QuickActionsPanel.Children.Add(button);
        }
        QuickActionsPanel.Visibility = QuickActionsPanel.Children.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommandAvailability(_view is null ? 0 : SelectedRaw().Count);
        RefreshToolbarOverflow();
    }

    void UpdateCommandAvailability(int selectedCount)
    {
        foreach (var button in LogicalDescendants(this).OfType<System.Windows.Controls.Primitives.ButtonBase>())
            CommandState.Update(button, selectedCount);
        ApplyModuleVisibility(selectedCount);
    }

    void ApplyModuleVisibility(int selectedCount)
    {
        foreach (var button in _toolbarTabs.SelectMany(tab => tab.Actions.Values).OfType<System.Windows.Controls.Primitives.ButtonBase>())
        {
            var id = CommandState.GetId(button);
            if (id is not ("restrictions.inventory" or "settings.schedules" or "remote.programs" or "classroom.appearance-settings")) continue;
            button.Visibility = ModuleUiPolicy.CommandVisible(_settings, id, selectedCount) &&
                (_settings.HostMode == HostModes.Admin || TeacherAllows(id)) ? Visibility.Visible : Visibility.Collapsed;
        }
        HideEmptyTabs();
        UpdateMoreSectionVisibility();
        if (CommandSearchPopup.IsOpen) UpdateCommandSearch();
    }

    readonly Dictionary<string, Button> _builtInQuickButtons = new(StringComparer.OrdinalIgnoreCase);

    void HostMode_Click(object sender, RoutedEventArgs e)
    {
        _settings.HostMode = _settings.HostMode == HostModes.Admin ? HostModes.Teacher : HostModes.Admin;
        _settings.Save();
        ApplyHostMode();
        WarnWhenAdminModeHasNoAdminKey();
    }

    /// <summary>Admin mode shows every action whatever key the Host holds; say so when the key is only a Teacher key.</summary>
    void WarnWhenAdminModeHasNoAdminKey()
    {
        if (_settings.HostMode != HostModes.Admin) return;
        var warning = KeyRoleSummary.AdminModeWarning(_clients.Select(client => client.Role));
        if (warning.Length > 0) MessageBox.Show(this, warning, "Admin mode", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    bool TeacherAllows(string commandId) =>
        TeacherCommandCatalog.AlwaysVisible.Contains(commandId) ||
        !TeacherCommandCatalog.NeedsAdminKey(commandId) && _settings.TeacherVisibleCommands.Contains(commandId, StringComparer.OrdinalIgnoreCase);

    void ApplyHostMode()
    {
        var admin = _settings.HostMode == HostModes.Admin;
        HostModeButton.Content = admin ? "Mode: Admin" : "Mode: Teacher";
        foreach (var definition in TeacherCommandCatalog.All)
        {
            foreach (var control in LogicalDescendants(this).OfType<ContentControl>().Where(control =>
                         control is Button or CheckBox or System.Windows.Controls.Primitives.ToggleButton && control.Content is string content &&
                         ButtonNameConverter.Clean(UiTranslator.Source(content)).Equals(definition.Label, StringComparison.OrdinalIgnoreCase)))
                // The display mode is only a preference of this Host. What a key may do is decided by the
                // computers, so an action a Teacher key is refused stays on screen and says so when used.
                control.Visibility = admin || TeacherAllows(definition.Id) ? Visibility.Visible : Visibility.Collapsed;
        }
        HostModeButton.Visibility = Visibility.Visible;
        BuildQuickActions();
        UpdateMoreSectionVisibility();
        HideEmptyTabs();
        if (CommandSearchPopup.IsOpen) UpdateCommandSearch();
    }

    /// <summary>A tab whose actions are all hidden (Teacher mode, Arrange toolbar) is not shown at all.</summary>
    void HideEmptyTabs()
    {
        foreach (var tab in _toolbarTabs)
        {
            int shown = tab.Top.Children.OfType<UIElement>().Concat(tab.More.Children.OfType<UIElement>()).Concat(tab.Overflow.Children.OfType<UIElement>())
                .Count(element => tab.Actions.ContainsValue(element) && element.Visibility == Visibility.Visible);
            tab.Item.Visibility = ToolbarArrangement.HidesTab(tab.Name, tab.Actions.Count, shown) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (MainTabs.SelectedItem is TabItem { Visibility: not Visibility.Visible })
            MainTabs.SelectedItem = MainTabs.Items.OfType<TabItem>().FirstOrDefault(item => item.Visibility == Visibility.Visible);
    }

    /// <summary>A tab of the main toolbar: its two rows, the places in the top row, and every action it can show.</summary>
    sealed class ToolbarTab
    {
        public required string Name { get; init; }
        public required TabItem Item { get; init; }
        public required Panel Top { get; init; }
        public required Panel More { get; init; }
        public required Panel Overflow { get; init; }
        public required Expander MoreSection { get; init; }
        public required FrameworkElement OverflowHeading { get; init; }
        public required FrameworkElement OtherHeading { get; init; }
        public required ToolbarOverflow Responsive { get; init; }
        /// <summary>The top row as built: an action's place is a slot for whichever action is arranged there; anything else stays put.</summary>
        public required List<UIElement> Slots { get; init; }
        public Dictionary<string, UIElement> Actions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DefaultTop { get; } = [];
        public List<string> DefaultMore { get; } = [];
    }

    readonly List<ToolbarTab> _toolbarTabs = [];

    /// <summary>Takes the tabs as MainWindow.xaml builds them; a tab without a "More" part gets an empty one.</summary>
    void CaptureToolbar()
    {
        foreach (var item in MainTabs.Items.OfType<TabItem>())
        {
            var name = UiTranslator.Source(item.Header as string ?? "");
            if (item.Content is WrapPanel lone)
            {
                item.Content = null;
                item.Content = new StackPanel { Children = { lone } };
            }
            if (item.Content is not StackPanel stack || stack.Children.OfType<WrapPanel>().FirstOrDefault() is not { } top) continue;
            if (stack.Children.OfType<Expander>().FirstOrDefault() is not { Content: WrapPanel more })
            {
                more = new WrapPanel { Style = top.Style };
                stack.Children.Add(new Expander { Header = "More actions", Margin = new Thickness(6, 0, 6, 6), Content = more });
            }
            var section = stack.Children.OfType<Expander>().First();
            var overflow = new WrapPanel { Style = top.Style };
            var overflowHeading = Ui.MutedText(Loc.T("More from the top bar"), new Thickness(6, 4, 6, 0));
            var otherHeading = Ui.MutedText(Loc.T("Other actions"), new Thickness(6, 4, 6, 0));
            section.Content = null;
            section.Content = new ScrollViewer
            {
                MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new StackPanel { Children = { overflowHeading, overflow, otherHeading, more } },
            };
            section.ToolTip = Loc.T("Commands that do not fit in the top bar are available here. Your saved toolbar order is unchanged.");
            var tab = new ToolbarTab
            {
                Name = name, Item = item, Top = top, More = more, Overflow = overflow, MoreSection = section,
                OverflowHeading = overflowHeading, OtherHeading = otherHeading,
                Responsive = new ToolbarOverflow(top, overflow, SearchLabel), Slots = top.Children.Cast<UIElement>().ToList(),
            };
            top.SizeChanged += (_, _) => RefreshToolbarOverflow();
            void Take(Panel row, List<string> defaults)
            {
                foreach (var action in row.Children.OfType<System.Windows.Controls.Primitives.ButtonBase>())
                {
                    // The mode button names the current mode, so it has no fixed label to be arranged by.
                    if (ReferenceEquals(action, HostModeButton) || action is not ContentControl { Content: string label }) continue;
                    var id = ToolbarArrangement.Id(name, UiTranslator.Source(label));
                    if (CommandState.GetId(action).Length == 0 && TeacherCommandCatalog.All.FirstOrDefault(command =>
                        command.Label.Equals(ToolbarArrangement.Label(id), StringComparison.OrdinalIgnoreCase)) is { } definition)
                        CommandState.SetId(action, definition.Id);
                    if (tab.Actions.TryAdd(id, action)) defaults.Add(id);
                }
            }
            Take(top, tab.DefaultTop);
            Take(more, tab.DefaultMore);
            _toolbarTabs.Add(tab);
        }
    }

    IReadOnlyList<ToolbarTabDefaults> ToolbarDefaults() =>
        _toolbarTabs.Select(tab => new ToolbarTabDefaults(tab.Name, tab.DefaultTop, tab.DefaultMore)).ToList();

    /// <summary>Lays every tab out as the teacher arranged it (or as the program does when nothing is saved).</summary>
    void ApplyToolbarLayout()
    {
        RestoreToolbarOverflow();
        if (_settings.ToolbarLayoutVersion < 1)
        {
            ToolbarArrangement.Relocate(_settings.ToolbarLayouts, ToolbarDefaults());
            _settings.ToolbarLayoutVersion = 1;
        }
        foreach (var tab in _toolbarTabs)
        {
            var layout = ToolbarArrangement.Resolve(tab.DefaultTop, tab.DefaultMore, _settings.ToolbarLayouts.GetValueOrDefault(tab.Name));
            tab.Top.Children.Clear();
            tab.More.Children.Clear();
            var top = new Queue<string>(layout.Top);
            foreach (var slot in tab.Slots)
            {
                if (!tab.Actions.ContainsValue(slot)) tab.Top.Children.Add(slot);
                else if (top.Count > 0) tab.Top.Children.Add(tab.Actions[top.Dequeue()]);
            }
            while (top.Count > 0) tab.Top.Children.Add(tab.Actions[top.Dequeue()]);
            foreach (var id in layout.More.OrderBy(id => SearchLabel((System.Windows.Controls.Primitives.ButtonBase)tab.Actions[id]), StringComparer.CurrentCultureIgnoreCase))
                tab.More.Children.Add(tab.Actions[id]);
        }
        CaptureToolbarOverflow();
        RefreshToolbarOverflow();
    }

    void RestoreToolbarOverflow()
    {
        foreach (var tab in _toolbarTabs) tab.Responsive.Restore();
    }

    void CaptureToolbarOverflow()
    {
        foreach (var tab in _toolbarTabs) tab.Responsive.Capture();
    }

    bool _reflowingToolbar;
    void RefreshToolbarOverflow()
    {
        if (_reflowingToolbar) return;
        _reflowingToolbar = true;
        try
        {
            bool changed = false;
            foreach (var tab in _toolbarTabs)
            {
                changed |= tab.Responsive.Reflow(tab.Top.ActualWidth);
                bool hasOverflow = tab.Overflow.Children.OfType<UIElement>().Any(element => element.Visibility == Visibility.Visible);
                tab.OverflowHeading.Visibility = hasOverflow ? Visibility.Visible : Visibility.Collapsed;
                tab.OtherHeading.Visibility = hasOverflow && tab.More.Children.OfType<UIElement>().Any(element => element.Visibility == Visibility.Visible)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateMoreSectionVisibility();
            foreach (var tab in _toolbarTabs.Where(tab => tab.Responsive.FocusMovedOut))
            {
                tab.MoreSection.ApplyTemplate();
                tab.MoreSection.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
            if (changed && CommandSearchPopup.IsOpen) UpdateCommandSearch();
        }
        finally { _reflowingToolbar = false; }
    }

    void ResetToolbar()
    {
        if (MessageBox.Show(this, "Put every tab of the toolbar back as Classroom Manager lays it out? Your own order and hidden actions are forgotten.",
                "Reset toolbar", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _settings.ToolbarLayouts.Clear();
        _settings.Save();
        ApplyToolbarLayout();
        ApplyHostMode();
    }

    void ArrangeToolbar_Click(object sender, RoutedEventArgs e) =>
        new ToolbarArrangeWindow(_settings, this, ToolbarDefaults(), () =>
        {
            ApplyToolbarLayout();
            ApplyHostMode();
        }).ShowDialog();

    void UpdateMoreSectionVisibility()
    {
        foreach (var section in LogicalDescendants(MainTabs).OfType<Expander>().Where(expander =>
                     expander.Header is string header && UiTranslator.Source(header).StartsWith("More ", StringComparison.OrdinalIgnoreCase)))
        {
            var hasVisibleAction = LogicalDescendants(section)
                .OfType<System.Windows.Controls.Primitives.ButtonBase>()
                .Any(action => action.Visibility == Visibility.Visible);
            section.Visibility = hasVisibleAction ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
        }
    }

    async void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string action }) return;
        if (!MainActionAvailability.CanExecute(CommandState.GetId((Button)sender), SelectedRaw().Count)) return;
        if (action.StartsWith("custom:", StringComparison.Ordinal))
        {
            var custom = _settings.CustomQuickActions.FirstOrDefault(item => item.Id == action["custom:".Length..]);
            if (custom is not null) await RunCustomQuickActionAsync(custom, Selected());
            return;
        }
        if (action == "exam")
        {
            await ConfirmExamModeChangeAsync(!_settings.ExamModeEnabled);
            return;
        }
        if (action == "send-files")
        {
            SendFiles_Click(sender, e);
            return;
        }
        var targets = Selected();
        if (targets.Count == 0) return;
        switch (action)
        {
            case "lock":
                await DoLockAsync(targets, "Screen locked", _settings.QuickActionMessage);
                break;
            case "unlock":
                await DoUnlockAsync(targets);
                break;
            case "message":
                await DoMessageAsync(targets, "Message from your teacher", _settings.QuickActionMessage, 0);
                break;
            case "restart":
            case "shutdown":
                if (MessageBox.Show(this, $"{(action == "restart" ? "Restart" : "Shut down")} {targets.Count} selected computer(s) in 2 minutes?",
                        "Quick power action", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                await DoPowerAsync(targets, action, 120,
                    action == "restart" ? "This computer will restart in 2 minutes. Please save your work."
                        : "This computer will shut down in 2 minutes. Please save your work.");
                break;
            case "cancel-power":
                await DoCancelPowerAsync(targets);
                break;
        }
    }

    static string CustomQuickActionIcon(string kind) => kind switch
    {
        CustomQuickActionKinds.OpenProgram => FluentIcons.Play,
        CustomQuickActionKinds.OpenWebsite => FluentIcons.Globe,
        CustomQuickActionKinds.Message => FluentIcons.Message,
        CustomQuickActionKinds.Lock => FluentIcons.Lock,
        CustomQuickActionKinds.Power => FluentIcons.Power,
        CustomQuickActionKinds.SyncFiles => FluentIcons.Folder,
        _ => FluentIcons.Play,
    };

    void CustomQuickActions_Click(object sender, RoutedEventArgs e) =>
        new CustomQuickActionsWindow(_settings, this, BuildQuickActions).ShowDialog();

    /// <summary>Runs a teacher-made quick action on the selected computers; anything that can lose work is confirmed first.</summary>
    async Task RunCustomQuickActionAsync(CustomQuickAction action, List<ClientInfo> targets)
    {
        var problem = action.Problem();
        if (problem.Length > 0)
        {
            MessageBox.Show(this, $"\"{action.Name}\" cannot run: {problem}", "Quick action", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "Select the computers to run the quick action on.", "Quick action", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        switch (action.Kind)
        {
            case CustomQuickActionKinds.OpenProgram:
                if (!EnsureFeatureSupported(targets, Cmd.ProgramOpen, "Open program")) return;
                await RunOnAsync(targets, action.Name, (client, session) => session.CallAtAsync(Levels.User, Cmd.ProgramOpen,
                    new JsonObject { ["program"] = action.Target, ["arguments"] = action.Arguments }));
                break;
            case CustomQuickActionKinds.OpenWebsite:
                // The browser and window state last chosen in Open website apply here too.
                await OpenWebsiteOnAsync(targets, new Uri(action.Target.Trim()).AbsoluteUri, action.Name);
                break;
            case CustomQuickActionKinds.Message:
                await DoMessageAsync(targets, "Message from your teacher", action.Text, 0);
                break;
            case CustomQuickActionKinds.Lock:
                await DoLockAsync(targets, "Screen locked", action.Text);
                break;
            case CustomQuickActionKinds.Power:
                if (MessageBox.Show(this, $"{action.PowerAction} {targets.Count} selected computer(s) in {action.DelaySeconds} s? Unsaved work may be lost.",
                        action.Name, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                if (action.PowerAction == "Sleep")
                {
                    if (action.DelaySeconds > 0)
                    {
                        Log($"{action.Name}: sleep in {action.DelaySeconds} s; keep Classroom Manager open until then.");
                        await Task.Delay(TimeSpan.FromSeconds(action.DelaySeconds));
                    }
                    await DoPowerAsync(targets, "sleep", 0, "");
                }
                else
                    await DoPowerAsync(targets, action.PowerAction == "Restart" ? "restart" : "shutdown", action.DelaySeconds,
                        action.Text.Length > 0 ? action.Text : "This computer will shut down soon. Please save your work.");
                break;
            case CustomQuickActionKinds.SyncFiles:
                await SyncFilesAsync(action, targets);
                break;
        }
    }

    static int ButtonDisplayIndex(string stored) => stored switch
    {
        "IconsOnly" => 1,
        "NamesOnly" => 2,
        _ => 0,
    };

    static string ButtonDisplayValue(string choice) => choice switch
    {
        "Icons only" => "IconsOnly",
        "Names only" => "NamesOnly",
        _ => "IconsAndNames",
    };
}
