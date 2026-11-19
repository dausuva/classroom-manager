// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Text.RegularExpressions;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// Lays out the Host's tool windows for real, with the app's own styles, and measures the gaps:
/// a button must not touch the button, box, list or table next to it. The windows are built but
/// never opened, so nothing connects to a computer; their content is laid out inside one hidden
/// window. A window this test cannot build (it needs arguments that only exist while the Host
/// runs) is skipped, and <see cref="Enough_windows_are_measured"/> keeps that from going unnoticed.
/// </summary>
[Collection(nameof(LanguageSwitching))]
public sealed class ButtonSpacingTests
{
    /// <summary>Less than this many pixels between two controls reads as "stuck together".</summary>
    const double MinimumGap = 4;

    /// <summary>Windows that read or write the real user's Host files when constructed, or are the main window itself.</summary>
    internal static readonly HashSet<string> NotBuiltHere = ["MainWindow", "WallpaperLibraryWindow", "LabKeysWindow", "CrashLogWindow", "FeedbackManagerWindow"];

    sealed record Audit(List<string> Touching, List<string> Measured, List<string> Skipped, List<string> WrongIcons)
    {
        /// <summary>The main window shown in Lithuanian: what stayed English, what lost its icon, and how much was translated.</summary>
        public List<string> Untranslated { get; } = [];
        public List<string> IconsLost { get; } = [];
        public int Translated { get; set; }
        public List<string> TranslatedCells { get; } = [];
        /// <summary>Controls a screen reader could only announce as "button" or "edit": no name, no text, no label.</summary>
        public List<string> Unnamed { get; } = [];
        /// <summary>Controls of the main window's tool tabs drawn taller than the room they were given, so their edge is cut off.</summary>
        public List<string> Clipped { get; } = [];
        /// <summary>Tables whose columns reach past the window's edge with no horizontal scrollbar to get to them (T-170).</summary>
        public List<string> HiddenColumns { get; } = [];
        /// <summary>Windows that opened narrower than their tables' columns and were widened, with the widths.</summary>
        public List<string> Widened { get; } = [];
        /// <summary>Windows still too narrow for their columns after widening although the screen had room.</summary>
        public List<string> TooNarrow { get; } = [];
    }

    static readonly Lazy<Audit> Result = new(RunOnStaThread);

    [Fact]
    public void No_button_touches_the_control_next_to_it()
    {
        var touching = Result.Value.Touching;
        Assert.True(touching.Count == 0,
            $"Controls closer than {MinimumGap} px. Give the button (or its neighbour) a Margin:\n" + string.Join("\n", touching));
    }

    [Fact]
    public void A_button_shows_the_icon_of_its_Tag_or_else_of_its_label_and_none_when_neither_names_one()
    {
        Assert.True(Result.Value.WrongIcons.Count == 0, string.Join("\n", Result.Value.WrongIcons));
    }

    [Fact]
    public void The_main_window_is_shown_in_Lithuanian_with_its_icons_and_without_touching_names()
    {
        var audit = Result.Value;
        Assert.True(audit.Untranslated.Count == 0, "Still English on a Lithuanian screen:\n" + string.Join("\n", audit.Untranslated));
        Assert.True(audit.IconsLost.Count == 0, "Icons changed when the labels were translated:\n" + string.Join("\n", audit.IconsLost));
        Assert.True(audit.Translated >= 10, $"Only {audit.Translated} button(s) of the main window were translated.");
        // The state of a computer is the program's text; its name, user and address are not.
        Assert.Contains("Prisijungęs", audit.TranslatedCells);
        Assert.Contains("Neprisijungęs (neatsako)", audit.TranslatedCells);
        Assert.Contains("PC-01", audit.TranslatedCells);
        Assert.Contains("student", audit.TranslatedCells);
        // A text that changes while the window is open is translated again.
        Assert.Contains("summary:3 reikia dėmesio", audit.TranslatedCells);
    }

    [Fact]
    public void Every_control_has_a_name_a_screen_reader_can_say()
    {
        var unnamed = Result.Value.Unnamed;
        Assert.True(unnamed.Count == 0,
            "These controls have no accessible name. Give each a label (Ui.Label / FormDialog do it), a text, or AutomationProperties.Name:\n" + string.Join("\n", unnamed));
    }

    [Fact]
    public void No_control_of_the_main_tool_tabs_is_cut_off()
    {
        var clipped = Result.Value.Clipped;
        Assert.True(clipped.Count == 0,
            "These controls are taller than the room the toolbar gives them, so their bottom edge is not drawn:\n" + string.Join("\n", clipped));
    }

    [Fact]
    public void A_table_wider_than_its_window_shows_a_horizontal_scrollbar()
    {
        var hidden = Result.Value.HiddenColumns;
        Assert.True(hidden.Count == 0,
            "These tables are wider than the window (here shown at the window's own size and again at a narrow 520 px) but offer no horizontal scrollbar:\n" + string.Join("\n", hidden));
    }

    [Fact]
    public void A_tool_window_opens_wide_enough_for_its_table()
    {
        var audit = Result.Value;
        Assert.True(audit.TooNarrow.Count == 0, "Still too narrow for the columns, with room on the screen:\n" + string.Join("\n", audit.TooNarrow));
        // The widening is not a no-op: several windows really did open narrower than their columns.
        Assert.True(audit.Widened.Count >= 3, "Only " + audit.Widened.Count + " window(s) were widened:\n" + string.Join("\n", audit.Widened));
    }

    [Fact]
    public void Enough_windows_are_measured()
    {
        var audit = Result.Value;
        Assert.True(audit.Measured.Count >= 15,
            $"Only {audit.Measured.Count} window(s) could be built and measured.\nSkipped:\n" + string.Join("\n", audit.Skipped));
    }

    static Audit RunOnStaThread()
    {
        Audit? audit = null;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { audit = Run(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return audit!;
    }

    static Audit Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var theme in new[] { "Light", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/ClassroomHost;component/Themes/{theme}.xaml", UriKind.Relative),
            });

        // Shown far off screen: layout needs a real window, the person running the tests does not need to see it.
        var stage = new Window
        {
            Left = -20000, Top = -20000, Width = 1100, Height = 750, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.None,
        };
        stage.Show();
        var audit = new Audit([], [], [], []);
        try
        {
            // The button template itself: what the icon part shows for a Tag, for a label alone, and for neither.
            foreach (var (tag, label, expected) in new (string?, string, string)[]
                     {
                         ("🔌", "Po_wer...", FluentIcons.Power), (FluentIcons.Key, "_Add key...", FluentIcons.Key),
                         (null, "_Refresh", FluentIcons.Refresh), (null, "Quux", ""),
                     })
            {
                var button = new Button { Content = label, Tag = tag };
                stage.Content = button;
                stage.UpdateLayout();
                var icon = (TextBlock)button.Template.FindName("Icon", button);
                var text = (ContentPresenter)button.Template.FindName("Label", button);
                if (icon.Text != expected || icon.IsVisible != (expected.Length > 0) || !text.IsVisible)
                    audit.WrongIcons.Add($"'{label}' (Tag {(tag is null ? "none" : "set")}): icon '{icon.Text}' visible={icon.IsVisible}, label visible={text.IsVisible}");
            }
            stage.Content = null;

            var windows = typeof(MainWindow).Assembly.GetTypes()
                .Where(type => typeof(Window).IsAssignableFrom(type) && !type.IsAbstract && !NotBuiltHere.Contains(type.Name))
                .OrderBy(type => type.Name);
            foreach (var type in windows)
            {
                Window window;
                try { window = Build(type, stage); }
                catch (Exception ex)
                {
                    audit.Skipped.Add($"{type.Name}: {(ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message}");
                    continue;
                }
                if (window.Content is not FrameworkElement content) { audit.Skipped.Add($"{type.Name}: no content"); continue; }

                // The window itself is never opened (opening it would start loading from a computer);
                // its content, resources and data context move to the stage and are laid out there.
                window.Content = null;
                stage.Resources = window.Resources;
                // What the app does for every window but the main one when it opens.
                Ui.CompactButtons(stage);
                stage.DataContext = window.DataContext;
                stage.Width = double.IsNaN(window.Width) ? 1100 : Math.Max(window.Width, window.MinWidth);
                stage.Height = double.IsNaN(window.Height) ? 750 : Math.Max(window.Height, window.MinHeight);
                stage.Content = content;
                stage.UpdateLayout();

                audit.Measured.Add(type.Name);
                audit.Touching.AddRange(Touching(content).Select(problem => $"{type.Name}: {problem}"));
                // A table is only as wide as its rows: an empty one has nothing to scroll sideways, so rows are put in.
                AddStandInRows(content);
                stage.UpdateLayout();
                // What the app does for every window when it opens: widen it to its tables.
                var openedAt = stage.ActualWidth;
                if (Ui.WidenToTables(stage)) { stage.UpdateLayout(); audit.Widened.Add($"{type.Name}: {openedAt:0} -> {stage.ActualWidth:0} px"); }
                if (Ui.WidthNeeded(stage) > stage.ActualWidth + 1 && stage.ActualWidth < SystemParameters.WorkArea.Width * 0.95 - 1)
                    audit.TooNarrow.Add($"{type.Name}: {stage.ActualWidth:0} px, its table wants {Ui.WidthNeeded(stage):0} px");
                audit.HiddenColumns.AddRange(ColumnsBeyondView(stage, content).Select(problem => $"{type.Name} at {stage.Width:0} px: {problem}"));
                // A person who makes the window narrower: every table must then scroll sideways.
                stage.Width = 520;
                stage.UpdateLayout();
                audit.HiddenColumns.AddRange(ColumnsBeyondView(stage, content).Select(problem => $"{type.Name} at 520 px: {problem}"));
                // What the app does for every window when it opens: an unnamed list takes the window's title.
                Ui.NameLists(content, window.Title);
                audit.Unnamed.AddRange(Unnamed(content).Select(problem => $"{type.Name}: {problem}"));
                stage.Content = null;
            }

            MeasureMainWindowMarkup(stage, audit, app);
        }
        finally { stage.Close(); }
        return audit;
    }

    /// <summary>Every tool tab of the main window with its "More" part open: a control whose drawn height is more than its slot is cut off.</summary>
    static List<string> ClippedInToolTabs(Window stage, FrameworkElement content)
    {
        var problems = new List<string>();
        if (Descendants(content).OfType<TabControl>().FirstOrDefault(tab => tab.Name == "MainTabs") is not { } tabs) return ["MainTabs not found"];
        for (int i = 0; i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            stage.UpdateLayout();
            foreach (var expander in Descendants(tabs).OfType<Expander>()) expander.IsExpanded = true;
            stage.UpdateLayout();
            foreach (var control in Descendants(tabs).OfType<Control>().Where(control => control is ButtonBase or ComboBox && control.IsVisible))
            {
                var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(control);
                var room = slot.Height - control.Margin.Top - control.Margin.Bottom;
                if (room + 0.5 < control.ActualHeight)
                    problems.Add($"{(tabs.Items[i] as TabItem)?.Header}: '{(control as ContentControl)?.Content ?? control.Name}' is {control.ActualHeight:0} px tall in {room:0} px");
            }
        }
        tabs.SelectedIndex = 0;
        return problems;
    }

    /// <summary>Set to a folder to get PNG pictures of the main window's layout in the light and the dark theme.</summary>
    const string SnapshotVariable = "CLASSROOM_UI_SNAPSHOTS";

    /// <summary>
    /// The main window cannot be constructed here (it loads the real user's settings and starts
    /// scanning), but its layout is all in MainWindow.xaml. The markup is loaded without its code:
    /// the class and the event handlers are taken out, everything that decides layout stays.
    /// </summary>
    static void MeasureMainWindowMarkup(Window stage, Audit audit, Application app)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "ClassroomManager.sln"))) folder = folder.Parent;
        var markup = File.ReadAllText(Path.Combine(folder!.FullName, "src", "ClassroomHost", "MainWindow.xaml"));
        markup = Regex.Replace(markup, @"\s(x:Class|Icon)=""[^""]*""", "");
        markup = Regex.Replace(markup, @"\s(Click|MouseDoubleClick|PreviewKeyDown|PreviewMouseLeftButtonUp|TextChanged|SelectionChanged|Closing|Loaded|KeyDown)=""\w+""", "");
        markup = markup.Replace("clr-namespace:ClassroomManager.Host\"", "clr-namespace:ClassroomManager.Host;assembly=ClassroomHost\"");

        FrameworkElement Load()
        {
            var window = (Window)XamlReader.Parse(markup);
            var loaded = (FrameworkElement)window.Content;
            window.Content = null;
            stage.Resources = window.Resources;
            stage.Width = 1500;
            stage.Height = 950;
            stage.Content = loaded;
            // A few rows and quick actions, so the table and the toolbar are measured with something in them.
            if (window.FindName("ClientGrid") is DataGrid grid)
                grid.ItemsSource = Enumerable.Range(1, 6).Select(number => new ClientInfo
                {
                    Name = $"PC-{number:00}", Room = "Lab 1", Address = $"192.168.1.{20 + number}", User = "student", KeyName = "Default lab",
                    Status = number == 4 ? "Offline (no reply)" : "Online", Level = number == 4 ? "" : number == 5 ? "warn" : "ok",
                    Ping = "4 ms", Clock = "+0.4 s", Uptime = "1h 44m", Version = "1.10", WindowsVersion = "Windows 10.0 (build 26200)",
                    InterfaceLanguage = "lt-LT", OpenWindows = "3 / 5", AppVersion = "1.2.0.281", LastSeen = "17:05:51", IsSelected = number < 3,
                }).ToList();
            if (window.FindName("BuiltInQuickActionsPanel") is Panel quick)
                foreach (var action in QuickActionCatalog.All.Take(3))
                    quick.Children.Add(new Button { Content = action.Label, Tag = action.Icon });
            if (window.FindName("QuickActionsPanel") is Panel custom)
                custom.Children.Add(new Button { Content = "Collect files...", Tag = FluentIcons.Folder });
            stage.UpdateLayout();
            return loaded;
        }
        var content = Load();

        audit.Measured.Add("MainWindow (markup)");
        audit.Touching.AddRange(Touching(content).Select(problem => $"MainWindow: {problem}"));
        audit.Unnamed.AddRange(Unnamed(content).Select(problem => $"MainWindow: {problem}"));
        // A window is never wider than its screen: on a 1024 px screen (a small laptop, the CI runner) the toolbars wrap onto a second
        // line, which the 1500 px layout above never shows. Measured at that width too, on every machine.
        stage.Width = 1000;
        stage.UpdateLayout();
        audit.Touching.AddRange(Touching(content).Select(problem => $"MainWindow at 1000 px: {problem}"));
        stage.Width = 1500;
        stage.UpdateLayout();

        if (Environment.GetEnvironmentVariable(SnapshotVariable) is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            foreach (var theme in new[] { "Light", "Dark" })
            {
                app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"/ClassroomHost;component/Themes/{theme}.xaml", UriKind.Relative) };
                stage.Background = (Brush)app.Resources["WindowBackgroundBrush"];
                stage.Foreground = (Brush)app.Resources["WindowForegroundBrush"];
                stage.UpdateLayout();
                stage.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                var picture = new RenderTargetBitmap((int)stage.ActualWidth, (int)stage.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                picture.Render(stage);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(picture));
                using var file = File.Create(Path.Combine(snapshots, $"main-window-{theme.ToLowerInvariant()}.png"));
                encoder.Save(file);
            }
            app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("/ClassroomHost;component/Themes/Light.xaml", UriKind.Relative) };
        }

        // The same window again in Lithuanian. Each button keeps its place, so the two passes compare one to one.
        static List<(string Label, string Icon)> Buttons(FrameworkElement root) => Controls(root).OfType<Button>()
            .Where(button => button.Content is string)
            .Select(button => ((string)button.Content, (button.Template?.FindName("Icon", button) as TextBlock)?.Text ?? "")).ToList();
        var english = Buttons(content);
        // Opens every tab and its "More" part, so it runs after the buttons above were counted.
        audit.Clipped.AddRange(ClippedInToolTabs(stage, content));
        stage.Content = null;
        UiTranslator.Initialize(Loc.Lithuanian);
        try
        {
            var translated = Load();
            // A window is translated when it opens and again whenever its layout changed and the program is idle.
            UiTranslator.Attach(stage);
            stage.UpdateLayout();
            var summary = Descendants(translated).OfType<TextBlock>().FirstOrDefault(block => block.Name == "SummaryText");
            if (summary is not null) summary.Text = "3 need attention";
            stage.UpdateLayout();
            stage.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            stage.UpdateLayout();
            if (summary is not null) audit.TranslatedCells.Add("summary:" + summary.Text);

            var lithuanian = Buttons(translated);
            if (lithuanian.Count != english.Count) audit.Untranslated.Add($"{english.Count} buttons in English but {lithuanian.Count} in Lithuanian");
            else
                for (int i = 0; i < english.Count; i++)
                {
                    if (lithuanian[i].Label != english[i].Label) audit.Translated++;
                    else if (Loc.Current.Translate(english[i].Label) != english[i].Label || english[i].Label.Any(char.IsLetter) && !english[i].Label.StartsWith("PC-"))
                        audit.Untranslated.Add(english[i].Label);
                    if (lithuanian[i].Icon != english[i].Icon) audit.IconsLost.Add($"'{english[i].Label}': icon '{english[i].Icon}' became '{lithuanian[i].Icon}'");
                }
            audit.TranslatedCells.AddRange(Descendants(translated).OfType<DataGridCell>()
                .SelectMany(cell => Descendants(cell).OfType<TextBlock>()).Select(block => block.Text).Distinct());
            audit.Touching.AddRange(Touching(translated).Select(problem => $"MainWindow in Lithuanian: {problem}"));

            if (Environment.GetEnvironmentVariable(SnapshotVariable) is { Length: > 0 } folderForPicture)
            {
                stage.Background = (Brush)app.Resources["WindowBackgroundBrush"];
                stage.Foreground = (Brush)app.Resources["WindowForegroundBrush"];
                stage.UpdateLayout();
                stage.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                var picture = new RenderTargetBitmap((int)stage.ActualWidth, (int)stage.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                picture.Render(stage);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(picture));
                using var file = File.Create(Path.Combine(folderForPicture, "main-window-lithuanian.png"));
                encoder.Save(file);
            }
        }
        finally
        {
            Loc.Use(Translation.None);
            stage.Content = null;
        }
    }

    /// <summary>
    /// A table whose columns are wider than the room it was given must show a horizontal scrollbar that a
    /// person can see: drawn, with some height, and inside the window (not pushed past its bottom edge by a
    /// table that is taller than the room it has), or the columns on the right cannot be reached.
    /// </summary>
    static IEnumerable<string> ColumnsBeyondView(Window stage, FrameworkElement root)
    {
        // A table with no rows has nothing to scroll sideways (WPF sizes the scrolling area by its rows), so it is left out.
        foreach (var table in Descendants(root).OfType<ItemsControl>().Where(control => control is ListView { View: GridView } or DataGrid && control.IsVisible && control.Items.Count > 0))
        {
            // A table's template holds more than one scroll viewer and several bars, most of them collapsed.
            var scrolls = Descendants(table).OfType<ScrollViewer>().ToList();
            if (scrolls.Count == 0) continue;
            var viewport = scrolls.Max(scroll => scroll.ViewportWidth);
            var columns = table is ListView { View: GridView grid }
                ? grid.Columns.Sum(column => column.ActualWidth)
                : ((DataGrid)table).Columns.Where(column => column.Visibility == Visibility.Visible).Sum(column => column.ActualWidth);
            if (columns <= viewport + 1) continue;

            var name = $"{table.GetType().Name} '{System.Windows.Automation.AutomationProperties.GetName(table)}{table.Name}'";
            var bar = Descendants(table).OfType<ScrollBar>().FirstOrDefault(candidate => candidate.Orientation == Orientation.Horizontal && candidate.IsVisible && candidate.ActualHeight > 4);
            if (bar is null)
            {
                yield return $"{name}: columns need {columns:0} px, the table shows {viewport:0} px, and no horizontal scrollbar is drawn";
                continue;
            }
            var bottom = bar.TransformToAncestor(stage).Transform(new Point(0, bar.ActualHeight)).Y;
            var inside = ((FrameworkElement)stage.Content).ActualHeight;
            if (bottom > inside + 1)
                yield return $"{name}: columns need {columns:0} px, but its horizontal scrollbar is at {bottom:0} px, below the window's {inside:0} px";
        }
    }

    static void AddStandInRows(FrameworkElement root)
    {
        foreach (var table in Descendants(root).OfType<ItemsControl>().Where(control => control is ListView { View: GridView } or DataGrid && control.ItemsSource is null))
            for (var i = 0; i < 3; i++) table.Items.Add(new object());
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    /// <summary>Builds a window with stand-in arguments; throws when its constructor needs something else.</summary>
    internal static Window Build(Type type, Window owner)
    {
        var client = new ClientInfo { Name = "PC-01", Address = "192.0.2.1", Room = "Lab 1" };
        foreach (var constructor in type.GetConstructors().OrderBy(candidate => candidate.GetParameters().Length))
        {
            var arguments = new List<object?>();
            foreach (var parameter in constructor.GetParameters())
            {
                var kind = parameter.ParameterType;
                if (kind == typeof(Window)) arguments.Add(owner);
                else if (kind == typeof(ClientInfo)) arguments.Add(client);
                else if (kind == typeof(HostSettings)) arguments.Add(new HostSettings());
                else if (kind.IsAssignableFrom(typeof(List<ClientInfo>))) arguments.Add(new List<ClientInfo> { client });
                else if (kind == typeof(string)) arguments.Add("Example");
                else if (kind == typeof(string[])) arguments.Add(new[] { "Selected computers (1)" });
                else if (kind == typeof(int)) arguments.Add(0);
                else if (kind == typeof(bool)) arguments.Add(false);
                else if (parameter.HasDefaultValue) arguments.Add(parameter.DefaultValue);
                else { arguments = null; break; }
            }
            if (arguments is not null) return (Window)constructor.Invoke(arguments.ToArray());
        }
        throw new InvalidOperationException("no constructor this test can call");
    }

    static bool IsMeasured(DependencyObject element) =>
        element is ButtonBase or TextBoxBase or PasswordBox or ComboBox or DataGrid or ListBox or TreeView &&
        element is not (GridViewColumnHeader or DataGridColumnHeader);

    /// <summary>The controls that count, without the parts other controls are made of (a scroll bar's buttons, a table's cells).</summary>
    static IEnumerable<FrameworkElement> Controls(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } element && IsMeasured(child))
            {
                yield return element;
                continue;
            }
            foreach (var deeper in Controls(child)) yield return deeper;
        }
    }

    /// <summary>
    /// The controls a person works with that have nothing to be announced by: no accessible name, no
    /// text of their own, no label pointing at them and no tooltip.
    /// </summary>
    static IEnumerable<string> Unnamed(FrameworkElement root)
    {
        foreach (var control in Controls(root))
        {
            if (System.Windows.Automation.AutomationProperties.GetName(control).Length > 0) continue;
            if (System.Windows.Automation.AutomationProperties.GetLabeledBy(control) is not null) continue;
            if (control is ContentControl { Content: string text } && text.Trim().Length > 0) continue;
            if (control is ContentControl { Content: AccessText or TextBlock }) continue;
            if (control.ToolTip is string tip && tip.Trim().Length > 0) continue;
            yield return string.IsNullOrEmpty(control.Name) ? control.GetType().Name : $"{control.GetType().Name} {control.Name}";
        }
    }

    static string Describe(FrameworkElement element) => element switch
    {
        ContentControl { Content: string text } => $"'{text.Replace("_", "")}'",
        _ => string.IsNullOrEmpty(element.Name) ? element.GetType().Name : $"{element.GetType().Name} {element.Name}",
    };

    static IEnumerable<string> Touching(FrameworkElement root)
    {
        var controls = Controls(root)
            .Select(control => (Control: control, Bounds: control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize))))
            .ToList();
        for (int i = 0; i < controls.Count; i++)
            for (int j = i + 1; j < controls.Count; j++)
            {
                var (first, second) = (controls[i], controls[j]);
                if (first.Control is not ButtonBase && second.Control is not ButtonBase) continue;
                // Side by side: they share a stretch of height and the space between them is too small.
                double sharedHeight = Math.Min(first.Bounds.Bottom, second.Bounds.Bottom) - Math.Max(first.Bounds.Top, second.Bounds.Top);
                double sharedWidth = Math.Min(first.Bounds.Right, second.Bounds.Right) - Math.Max(first.Bounds.Left, second.Bounds.Left);
                double besides = Math.Max(first.Bounds.Left, second.Bounds.Left) - Math.Min(first.Bounds.Right, second.Bounds.Right);
                double above = Math.Max(first.Bounds.Top, second.Bounds.Top) - Math.Min(first.Bounds.Bottom, second.Bounds.Bottom);
                if (sharedHeight > 2 && besides is > -1 and < MinimumGap)
                    yield return $"{Describe(first.Control)} and {Describe(second.Control)} are {besides:0.#} px apart side by side";
                else if (sharedWidth > 2 && above is > -1 and < MinimumGap)
                    yield return $"{Describe(first.Control)} and {Describe(second.Control)} are {above:0.#} px apart, one above the other";
            }
    }
}
