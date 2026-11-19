// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClassroomManager.Host;

/// <summary>Small shared helpers for code-built WPF windows.</summary>
public static class Ui
{
    public static TextBlock StatusText(Thickness? margin = null)
    {
        var text = new TextBlock
        {
            Margin = margin ?? new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        text.SetResourceReference(FrameworkElement.StyleProperty, "StatusTextStyle");
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "Copy status" };
        copy.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(text.Text)) Clipboard.SetText(text.Text); };
        menu.Items.Add(copy);
        text.ContextMenu = menu;
        return text;
    }

    /// <summary>
    /// Keeps the controls of a toolbar row apart: every child gets at least <paramref name="gap"/>
    /// to its right and below it, so neighbours do not touch and neither do the rows when the panel
    /// wraps in a narrow window. Margins that are already larger are kept. Call it after the last child is added.
    /// </summary>
    /// <summary>
    /// The nearest element of type <typeparamref name="T"/> around <paramref name="child"/>. A click can land on a
    /// piece of text (a Run), which is not a visual and has only a logical parent: going through VisualTreeHelper
    /// alone threw there and closed the Host when a computer's text was double-clicked.
    /// </summary>
    public static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = child is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }

    /// <summary>"Expand all" and "Collapse all" for a tree with one branch per computer.</summary>
    public static (Button Expand, Button Collapse) ExpandCollapseButtons(TreeView tree)
    {
        var expand = new Button { Content = "E_xpand all", Tag = FluentIcons.Down, ToolTip = "Expand all — open every computer's branch to see what was found." };
        var collapse = new Button { Content = "Colla_pse all", Tag = FluentIcons.Up, ToolTip = "Collapse all — close every computer's branch." };
        expand.Click += (_, _) => SetExpanded(tree, true);
        collapse.Click += (_, _) => SetExpanded(tree, false);
        return (expand, collapse);
    }

    /// <summary>Opens or closes the top branches; works for trees of TreeViewItems and for trees bound to data.</summary>
    public static void SetExpanded(TreeView tree, bool expanded)
    {
        foreach (var item in tree.Items)
            if ((item as TreeViewItem ?? tree.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem) is { } branch)
                branch.IsExpanded = expanded;
    }

    /// <summary>
    /// Spaces a row of buttons; a tick box in the row takes the button-like look so it reads as one of them.
    /// Rows without a button (a column of options in a form) are left as they are.
    /// </summary>
    public static T Spaced<T>(T panel, double gap = 6) where T : Panel
    {
        foreach (var child in panel.Children.OfType<FrameworkElement>())
            child.Margin = new Thickness(child.Margin.Left, child.Margin.Top, Math.Max(child.Margin.Right, gap), Math.Max(child.Margin.Bottom, gap));
        if (panel.Children.OfType<Button>().Any())
            foreach (var box in panel.Children.OfType<CheckBox>())
                box.SetResourceReference(FrameworkElement.StyleProperty, "ToolbarCheckBoxStyle");
        return panel;
    }

    /// <summary>
    /// A window's explanation: the text in full the first few times the window opens, then folded behind an info icon (hover or Tab to it
    /// to read it as a tool tip, Enter or a click to unfold it). New users read it where it matters; later it no longer fills the window.
    /// </summary>
    public static FrameworkElement Explanation(string text, Thickness? margin = null)
    {
        var body = MutedText(text);
        var icon = new Button
        {
            Tag = FluentIcons.Info,
            Content = "",
            ToolTip = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            Padding = new Thickness(4, 0, 4, 0),
            MinWidth = 0,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        System.Windows.Automation.AutomationProperties.SetName(icon, "About this window");
        System.Windows.Automation.AutomationProperties.SetHelpText(icon, text);
        ToolTipService.SetShowsToolTipOnKeyboardFocus(icon, true);
        icon.Click += (_, _) => body.Visibility = body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        var panel = new DockPanel { Margin = margin ?? new Thickness(0), LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        panel.Children.Add(icon);
        panel.Children.Add(body);
        panel.Loaded += (_, _) =>
        {
            if (Window.GetWindow(panel) is not { } window) return;
            var preferences = UiPreferences.Instance;
            // One count per opening of the window, however many explanations it has; they all fold or unfold together.
            var before = (int)Openings.GetValue(window, opened => preferences.CountOpening?.Invoke(opened.GetType().Name) ?? 0);
            if (!preferences.AlwaysShowExplanations && before >= UiPreferences.ExplanationShownTimes) body.Visibility = Visibility.Collapsed;
        };
        return panel;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> Openings = new();

    public static TextBlock MutedText(string text, Thickness? margin = null)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return block;
    }

    /// <summary>
    /// Gives the lists and tables of a window that have no name of their own the window's title, so a
    /// screen reader says "Programs - PC-01, table" instead of only "table". Most windows are one
    /// list with buttons around it, which is why the title is the right name.
    /// </summary>
    public static void NameLists(DependencyObject root, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is ListView or DataGrid or TreeView or ListBox && current is FrameworkElement list &&
                AutomationProperties.GetName(list).Length == 0 && AutomationProperties.GetLabeledBy(list) is null)
                AutomationProperties.SetName(list, title);
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()) pending.Push(child);
        }
    }

    public static Label Label(string text, FrameworkElement target, Thickness? margin = null)
    {
        AutomationProperties.SetName(target, text.Replace("_", ""));
        return new Label
        {
            Content = text,
            Target = target,
            Padding = new Thickness(0),
            Margin = margin ?? new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static void RefreshOnF5(Window window, Func<Task> refresh)
    {
        window.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.F5 || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            await refresh();
        };
    }

    /// <summary>
    /// Ctrl+A anywhere in the window selects every row the list shows now (so a filtered list selects only the matching rows).
    /// In a text box Ctrl+A keeps selecting its text, and the list must allow more than one selected row.
    /// </summary>
    public static void SelectAllOnCtrlA(Window window, ListView list)
    {
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.A || Keyboard.Modifiers != ModifierKeys.Control) return;
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or ComboBox) return;
            if (list.SelectionMode == SelectionMode.Single) return;
            e.Handled = true;
            list.SelectAll();
            if (list.SelectedItems.Count > 0) list.Focus();
        };
    }

    /// <summary>
    /// Replaces a remote collection without discarding selections that still identify the same
    /// domain objects. Callers supply a stable, resource-specific key; this deliberately avoids
    /// reference equality because every refresh materializes new view-model instances.
    /// </summary>
    public static void ReplaceItemsPreservingSelection<T>(
        ListView list, IReadOnlyList<T> items, Func<T, string> key, bool preserveSelection = true)
    {
        var selectedKeys = preserveSelection
            ? list.SelectedItems.Cast<T>().Select(key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The sort belongs to the view of the old rows; without carrying it over, every refresh would undo
        // the order the teacher clicked.
        var sorts = list.ItemsSource is null ? [] : CollectionViewSource.GetDefaultView(list.ItemsSource)?.SortDescriptions.ToList() ?? [];

        list.ItemsSource = items;
        if (sorts.Count > 0 && CollectionViewSource.GetDefaultView(items) is { CanSort: true } view)
            using (view.DeferRefresh())
                foreach (var sort in sorts) view.SortDescriptions.Add(sort);
        T? firstRestored = default;
        foreach (var item in items)
        {
            if (!selectedKeys.Contains(key(item))) continue;
            list.SelectedItems.Add(item);
            firstRestored ??= item;
        }

        if (firstRestored is not null) list.ScrollIntoView(firstRestored);
    }

    /// <summary>
    /// Lower buttons for a dialog or tool window: the main toolbar's buttons are tall to be easy to hit, but
    /// in a smaller window that height makes every row of buttons bulky. Based on the app's own button style,
    /// so everything else stays the same; a button that sets its own height or padding keeps it.
    /// </summary>
    public static void CompactButtons(FrameworkElement root)
    {
        if (root.Resources.Contains(typeof(Button)) || root.TryFindResource(typeof(Button)) is not Style app) return;
        root.Resources[typeof(Button)] = new Style(typeof(Button), app)
        {
            Setters =
            {
                new Setter(FrameworkElement.MinHeightProperty, 28.0),
                new Setter(Control.PaddingProperty, new Thickness(10, 3, 10, 3)),
            },
        };
    }

    /// <summary>
    /// Takes rows out of a list whose rows are an <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>,
    /// after a short fade where Windows shows animations. Nothing is reloaded, so the sort, the scroll position
    /// and the other rows' selection stay as they are.
    /// </summary>
    public static async Task FadeOutAndRemoveAsync<T>(ListView list, IReadOnlyCollection<T> rows)
    {
        if (list.ItemsSource is not System.Collections.ObjectModel.ObservableCollection<T> source || rows.Count == 0) return;
        var faded = new List<UIElement>();
        if (SystemParameters.ClientAreaAnimation)
        {
            var done = new List<Task>();
            foreach (var row in rows)
            {
                if (list.ItemContainerGenerator.ContainerFromItem(row) is not UIElement container) continue;
                var finished = new TaskCompletionSource();
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
                fade.Completed += (_, _) => finished.TrySetResult();
                container.BeginAnimation(UIElement.OpacityProperty, fade);
                faded.Add(container);
                done.Add(finished.Task);
            }
            await Task.WhenAny(Task.WhenAll(done), Task.Delay(1000));
        }
        foreach (var row in rows) source.Remove(row);
        // A row's box is used again for another row when the list scrolls; it must not stay invisible.
        foreach (var container in faded) container.BeginAnimation(UIElement.OpacityProperty, null);
    }

    /// <summary>
    /// Opens a window wide enough for its tables (T-170). A tool window used to open at a fixed width that was
    /// often less than its columns, so the last columns were cut off until the person dragged the window wider.
    /// Done once the window has been laid out; a window is never made narrower than it opened.
    /// </summary>
    public static void FitToTables(Window window) =>
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => WidenToTables(window));

    /// <summary>
    /// Widens the window to its tables' columns, at most to 95 % of the screen's working area, keeping it
    /// centred where it was. Returns whether the width changed.
    /// </summary>
    public static bool WidenToTables(Window window)
    {
        if (window.WindowState != WindowState.Normal || window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight || window.ActualWidth <= 0) return false;
        var area = SystemParameters.WorkArea;
        var target = Math.Min(WidthNeeded(window), area.Width * 0.95);
        if (target <= window.ActualWidth + 1) return false;

        var grow = target - window.ActualWidth;
        window.Width = target;
        // A window that is on the screen stays on it; one that is not (a test's stage) is left where it is.
        if (window.Left + window.ActualWidth > area.Left && window.Left < area.Right)
            window.Left = Math.Max(area.Left, Math.Min(window.Left - grow / 2, area.Right - target));
        return true;
    }

    /// <summary>The window width at which every large table shows all its columns, or 0 when there is no such table.</summary>
    public static double WidthNeeded(Window window)
    {
        var needed = 0.0;
        foreach (var table in VisualDescendants(window).OfType<Control>().Where(control => control is ListView { View: GridView } or DataGrid && control.IsVisible))
        {
            // Only a table that is most of the window; a small list beside other controls does not decide its width.
            if (table.ActualWidth < window.ActualWidth * 0.6) continue;
            var viewport = VisualDescendants(table).OfType<ScrollViewer>().Select(scroll => scroll.ViewportWidth).DefaultIfEmpty(0).Max();
            if (viewport <= 0) continue;
            // What the window spends on margins, borders and the frame, and room for a vertical scrollbar.
            needed = Math.Max(needed, ColumnsWidth(table) + (window.ActualWidth - viewport) + 20);
        }
        return needed;
    }

    /// <summary>The width all the visible columns of a table want.</summary>
    public static double ColumnsWidth(Control table) => table switch
    {
        ListView { View: GridView grid } => grid.Columns.Sum(column => Math.Max(column.ActualWidth, double.IsNaN(column.Width) ? 0 : column.Width)),
        DataGrid data => data.Columns.Where(column => column.Visibility == Visibility.Visible).Sum(column =>
            column.Width.IsAbsolute ? column.Width.Value : Math.Max(column.ActualWidth, Math.Max(column.MinWidth, 80))),
        _ => 0,
    };

    static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var deeper in VisualDescendants(child)) yield return deeper;
        }
    }
}

/// <summary>Turns a button's access-key label into a concise accessible name.</summary>
public sealed class ButtonNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text ? Clean(text) : "Command";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    internal static string Clean(string text) => text.Replace("_", "").Trim();
}

/// <summary>Supplies supplemental action help while preserving the product's all-buttons hint requirement.</summary>
public sealed class ButtonHelpConverter : IValueConverter
{
    static readonly Dictionary<string, string> Help = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Refresh"] = "reload the latest remote data",
        ["Browse..."] = "choose a path with the Windows picker",
        ["Up"] = "open the parent folder",
        ["Go"] = "open the path entered above",
        ["Upload files..."] = "copy local files to the remote folder",
        ["Download..."] = "save selected remote files locally",
        ["New folder..."] = "create a folder in the current location",
        ["Rename..."] = "rename the selected item",
        ["Delete"] = "delete the selected item after confirmation",
        ["Delete..."] = "delete the selected item after confirmation",
        ["Create..."] = "create a new item",
        ["Reset password..."] = "set a new password for the selected account",
        ["Enable/disable"] = "change the selected item's enabled state",
        ["Enable / disable"] = "change the selected item's enabled state",
        ["Enable"] = "enable the selected item",
        ["Disable"] = "disable the selected item",
        ["Run"] = "execute the entered command",
        ["Run now"] = "start the selected task now",
        ["Start"] = "start the selected remote shell",
        ["End task"] = "terminate the selected remote process",
        ["Windows Update..."] = "check, select, install, pause, resume, or restart Windows updates",
        ["Uninstall selected"] = "run the selected remote uninstall commands",
        ["Uninstall program"] = "review the matched program for uninstall",
        ["Block process"] = "prevent the selected process from running",
        ["Remove autorun entry only"] = "remove the startup hook but keep the program",
        ["Add..."] = "create a new entry",
        ["Add/edit value..."] = "create or change a registry value",
        ["Delete selected..."] = "delete the selected registry entry after confirmation",
        ["Export key..."] = "save the current registry key to a file",
        ["Import .reg file..."] = "apply registry entries from a file",
        ["Cancel"] = "close without applying changes",
        ["Use selected image"] = "apply the selected library image",
        ["Use selected solid color"] = "apply the selected Windows desktop color",
        ["Use Windows default wallpaper"] = "apply the stock wallpaper installed with Windows",
        ["Restore previous wallpaper..."] = "restore the wallpaper saved before Classroom Manager changed it",
        ["Managed wallpapers..."] = "inspect and clean up managed wallpaper copies on one computer",
        ["Windows themes..."] = "list and apply themes available on one computer",
        ["Windows appearance & language..."] = "change Windows color mode or interface language on selected computers",
        ["Sign in user..."] = "restart selected computers that nobody is using and sign one account in on them",
        ["Screen saver..."] = "turn off, choose, or install a screen saver for signed-in users on selected computers",
        ["Browsers..."] = "stop new browser profiles, clear browsing data, or remove extra profiles in Chrome, Edge and Firefox on selected computers",
        ["Websites in use..."] = "see which websites are open in the browsers of selected computers and block the ones you tick",
        ["Recycle Bin..."] = "empty the Recycle Bin of signed-in users on selected computers, or give it its Windows name back",
        ["Add images..."] = "copy images into the wallpaper library",
        ["Remove selected"] = "remove the selected library copy",
        ["Host settings..."] = "configure this teacher console",
        ["Crash logs..."] = "review or clear locally captured Host crash reports",
        ["Lab keys..."] = "manage named shared keys for multiple labs",
        ["Access keys..."] = "add or revoke named Teacher and Admin keys on selected computers",
        ["Rotate shared key..."] = "replace the administration key safely",
        ["Help"] = "open the teacher guide with pictures",
        ["Message to administrator..."] = "leave a note for whoever looks after this lab",
        ["Messages from teachers..."] = "read the notes teachers left for the administrator",
        ["Setup assistant..."] = "take a key or a settings file, find the computers and put them in a room",
        ["Languages..."] = "see the available languages or add one you translated yourself",
        ["Export configuration..."] = "save every setting and key of this Host in one password-protected file",
        ["Import configuration..."] = "replace this Host's settings and keys with an exported configuration file",
        ["Settings backups..."] = "see the automatic copies of this Host's settings and keys and put one back",
        ["Export teacher settings..."] = "create a Teacher key on selected computers and save it with their list and presets in one file",
        ["Import teacher settings..."] = "take the key, computers and presets from a file your administrator exported",
        ["About..."] = "show product and version information",
        ["Open-source licenses..."] = "review product and third-party license notices",
        ["Feedback..."] = "review and share gathered product feedback",
        ["Feedback"] = "describe this screen for product quality review",
        ["Send feedback now"] = "save this feedback and open an addressed email draft",
        ["Save for later"] = "store this feedback locally for review in Settings",
        ["Share gathered feedback"] = "copy the full report and open an addressed email draft",
        ["Copy"] = "copy the displayed text to the clipboard",
        ["Close"] = "close this window",
    };

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string raw) return null;
        var label = ButtonNameConverter.Clean(UiTranslator.Source(raw));
        var shown = ButtonNameConverter.Clean(Loc.T(UiTranslator.Source(raw)));
        return Help.TryGetValue(label, out var help)
            ? $"{shown} — {Loc.T(help)}"
            : $"{shown} — {Loc.T("activate this command")}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
