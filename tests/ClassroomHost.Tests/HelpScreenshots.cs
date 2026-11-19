// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// Takes a picture of every window of the teacher app, in English and in Lithuanian, for the help
/// pages in docs\help. It opens the real main window far off screen with six made-up computers,
/// presses every button and photographs whatever opens, then closes it without confirming anything.
///
/// It does nothing unless asked, because it takes a few minutes and rewrites the pictures:
///
///   $env:CLASSROOM_HELP_SCREENSHOTS = "1"
///   dotnet test tests\ClassroomHost.Tests --filter "FullyQualifiedName~HelpScreenshots"
///
/// The made-up computers have names that can never be found on any network, so nothing is sent to
/// a real computer and a window that loads from a computer shows that it could not.
/// Settings come from the scratch folder every test uses (see TestDataFolder), never the real one.
/// </summary>
[Collection(nameof(LanguageSwitching))]
public class HelpScreenshots
{
    const string Variable = "CLASSROOM_HELP_SCREENSHOTS";

    /// <summary>
    /// Retakes only some pictures: names or parts of names, separated by commas ("settings-schedules,settings-host-settings"). The other
    /// pictures and the index stay as they are; only the matching files are written over.
    /// </summary>
    const string OnlyVariable = "CLASSROOM_HELP_SCREENSHOTS_ONLY";

    static readonly string[]? Only = Environment.GetEnvironmentVariable(OnlyVariable) is { Length: > 0 } only
        ? only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;

    /// <summary>True when a picture is taken in this run: every picture, or those <see cref="OnlyVariable"/> names.</summary>
    static bool Wanted(string slug) => Only is null || Only.Any(part => slug.Contains(part, StringComparison.OrdinalIgnoreCase));

    /// <summary>Windows whose lower part is out of sight in the first picture: a second one is taken with the window scrolled to its end (the module list of Host settings).</summary>
    static readonly HashSet<string> AlsoScrolledToEnd = new(StringComparer.OrdinalIgnoreCase) { "settings-host-settings" };

    /// <summary>Buttons that act at once instead of opening something, or that change what the next pictures show.</summary>
    static readonly HashSet<string> NotPressed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Scan network", "Refresh status", "Wake up", "Remove", "Remove offline", "Unlock", "Hide messages", "Reset mouse",
        "All", "Online", "None", "Mode: Admin", "Mode: Teacher", "Open log folder", "Retry failed", "Cancel",
        "Clear completed", "Copy details", "Copy all", "Open results", "Close results", "Review warnings",
        "Lock screen", "Unlock screen", "Show message", "Toggle Exam mode", "Restart in 2 minutes", "Shut down in 2 minutes",
        "Cancel pending shutdown", "Approve certificate...",
    };

    /// <summary>Actions that open a different window for one computer than for several, or need exactly one.</summary>
    static readonly HashSet<string> AlsoWithOneComputer = new(StringComparer.OrdinalIgnoreCase)
    {
        "Terminal...", "Registry...", "Managed wallpapers...", "Windows themes...", "Programs...", "Processes...",
        "Windows Update...", "Browse files...", "Scheduled tasks...", "Autoruns...", "Local accounts...", "Signed-in accounts...",
    };

    sealed record Picture(string Slug, string Title);

    static List<string>? EnglishHeaders;

    [Fact]
    public void Pictures_of_every_window_in_English_and_Lithuanian()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1") return;

        var folder = Path.Combine(StringExtractor.RepositoryRoot(), "docs", "help", "screenshots");
        Dictionary<string, List<Picture>> taken = new();
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var (language, code) in new[] { (Loc.English, "en"), (Loc.Lithuanian, "lt") })
                    taken[code] = Capture(language, Path.Combine(folder, code));
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();

        if (Only is not null)
        {
            // Some pictures retaken: the rest, and the index of all of them, stay.
            Assert.True(taken["en"].Count > 0, $"No picture matches {OnlyVariable}={string.Join(",", Only)}.");
            Assert.Equal(taken["en"].Select(picture => picture.Slug).Order(), taken["lt"].Select(picture => picture.Slug).Order());
            return;
        }
        WriteIndex(Path.Combine(folder, ".."), taken);
        Assert.True(taken["en"].Count >= 40, $"Only {taken["en"].Count} pictures were taken.");
        Assert.Equal(taken["en"].Select(picture => picture.Slug).Order(), taken["lt"].Select(picture => picture.Slug).Order());
    }

    // ---- one language -------------------------------------------------------------------------------

    static List<Picture> Capture(string language, string folder)
    {
        // A full run starts from an empty folder, so a picture of a window that is gone goes too; a partial run writes over its own files only.
        if (Only is null && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var pictures = new List<Picture>();

        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count == 0)
            foreach (var theme in new[] { "Light", "Controls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/ClassroomHost;component/Themes/{theme}.xaml", UriKind.Relative) });
        // A window that fails while loading from a computer that is not there must not stop the run.
        app.DispatcherUnhandledException += (_, e) => e.Handled = true;

        // Settings of an imaginary lab, in a folder of their own inside the tests' scratch folder.
        var data = Path.Combine(TestDataFolder.Scratch, "help-" + Path.GetFileName(folder));
        Directory.CreateDirectory(data);
        var before = HostSettings.DataFolder;
        HostSettings.DataFolder = data;
        new HostSettings
        {
            Psk = "DEMO1-DEMO2-DEMO3-DEMO4-DEMO5", DefaultKeyName = "Lab 1", Language = language, HostMode = HostModes.Admin,
            AutoRefreshEnabled = false, RunSchedules = false, CrashLoggingEnabled = false, ActivityLogEnabled = false,
            ExamModeDomains = ["games.example.net", "video.example.org"], ExamModeApps = ["discord.exe"],
            DefaultWebsiteBlocklist = ["games.example.net"], BlockedAppCatalog = ["discord.exe", "steam.exe"],
            SavedWebsites = ["https://school.example.org"],
            Computers = Enumerable.Range(1, 6).Select(number => new SavedComputer
            {
                // A name that can never resolve (.invalid is reserved for that): shown whatever network this computer
                // is on, and nothing can be sent to it.
                Address = $"pc-{number:00}.demo.invalid", Port = 9, Name = $"PC-{number:00}", Room = "Lab 1", Mac = $"00155D0000{number:00}",
            }).ToList(),
        }.Save();

        UiHooks.QuietStart = true;
        UiHooks.MessageBox = (_, _, buttons) => buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OK => MessageBoxResult.OK,
            _ => MessageBoxResult.Cancel,
        };
        UiTranslator.Initialize(language);
        MainWindow? main = null;
        try
        {
            main = new MainWindow { Left = -20000, Top = -20000, Width = 1400, Height = 900, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            main.Show();
            Style(main);
            Wait(400);
            // The pictures should not show the version of the test runner or the network of whoever took them.
            main.Title = "Classroom Manager";
            if (main.FindName("NetworkScopeText") is TextBlock network) network.Text = "Network: 192.168.1.0/24 (automatic LAN)";

            var clients = (ObservableCollection<ClientInfo>)typeof(MainWindow).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
            var refresh = typeof(MainWindow).GetMethod("UpdateCollectionState", BindingFlags.NonPublic | BindingFlags.Instance)!;
            void Select(int count)
            {
                for (int i = 0; i < clients.Count; i++) clients[i].IsSelected = i < count;
                // What a click on a checkbox does next: the target banner and the buttons follow the selection.
                refresh.Invoke(main, null);
                Wait(150);
            }
            for (int i = 0; i < clients.Count; i++)
            {
                var client = clients[i];
                client.Status = i == 3 ? "Offline (no reply)" : i == 4 ? "Online - nobody signed in" : "Online";
                client.Level = i == 3 ? "" : i == 4 ? "idle" : "ok";
                client.User = i is 3 or 4 ? "" : $"student{i + 1}";
                client.Version = Protocol.Version;
                client.Role = "Admin";
                client.CapabilitiesAdvertised = true;
                foreach (var capability in Protocol.Capabilities) client.Capabilities.Add(capability);
                client.Ping = i == 3 ? "" : $"{2 + i} ms";
                client.Clock = i == 3 ? "" : "+0.2 s";
                client.Uptime = i == 3 ? "" : $"{1 + i}h 12m";
                client.WindowsVersion = "Windows 11 24H2";
                client.InterfaceLanguage = language;
                client.AppVersion = "1.2.0.301";
                client.LastSeen = "09:15:02";
                client.KeyName = "Lab 1";
            }
            Select(3);

            // The main window, tab by tab, then its three views.
            var tabs = (TabControl)main.FindName("MainTabs");
            // The tabs are named once, in the English pass: two English words can have the same Lithuanian one ("Room", "Classroom" and "Lab"
            // are all "Klasė"), so turning the Lithuanian header back into English is not reliable, and both languages must use the same picture names.
            var headers = EnglishHeaders ??= tabs.Items.OfType<TabItem>().Select(tab => UiTranslator.Source(tab.Header as string ?? "")).ToList();
            for (int i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                Wait(250);
                foreach (var expander in Descendants(main).OfType<Expander>()) expander.IsExpanded = true;
                Wait(250);
                Save(main, pictures, folder, $"main-{i + 1:00}-{Slug(headers[i])}");
            }
            tabs.SelectedIndex = 0;
            var show = typeof(MainWindow).GetMethod("ShowWorkspaceView", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (var (index, name) in new[] { (1, "operations"), (2, "log") })
            {
                show.Invoke(main, [index]);
                Wait(250);
                Save(main, pictures, folder, $"main-view-{name}");
            }
            show.Invoke(main, [0]);

            // Every button of every tab: press it, photograph what opens, close it again.
            var pressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                Wait(250);
                foreach (var expander in Descendants(main).OfType<Expander>()) expander.IsExpanded = true;
                Wait(250);
                var tab = Slug(headers[i]);
                foreach (var button in Descendants(tabs).OfType<Button>().Where(button => button.IsVisible).ToList())
                {
                    if (button.Content is not string content) continue;
                    var label = UiTranslator.Source(content).Replace("_", "").Trim();
                    if (NotPressed.Contains(label) || !pressed.Add(label)) continue;
                    var slug = $"{tab}-{Slug(label)}";
                    if (!Wanted(slug) && !Wanted(slug + "-one-computer")) continue;
                    Select(3);
                    Press(app, main, button, pictures, folder, $"{tab}-{Slug(label)}");
                    if (!AlsoWithOneComputer.Contains(label)) continue;
                    Select(1);
                    Press(app, main, button, pictures, folder, $"{tab}-{Slug(label)}-one-computer");
                }
            }
        }
        finally
        {
            try { main?.Close(); } catch (InvalidOperationException) { }
            foreach (var window in app.Windows.Cast<Window>().ToList())
                try { window.Close(); } catch (InvalidOperationException) { }
            Wait(200);
            UiHooks.QuietStart = false;
            UiHooks.MessageBox = null;
            Loc.Use(Translation.None);
            HostSettings.DataFolder = before;
        }
        return pictures;
    }

    /// <summary>Presses a button and photographs every window that opens because of it; a file or folder picker of Windows is closed unseen.</summary>
    static void Press(Application app, Window main, Button button, List<Picture> pictures, string folder, string name)
    {
        if (!button.IsEnabled) return;
        var known = app.Windows.Cast<Window>().ToHashSet();
        var frame = new DispatcherFrame();
        int ticks = 0, seenAt = -1;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            ticks++;
            if (ticks >= 3) CloseSystemDialogs();
            var opened = app.Windows.Cast<Window>().Where(window => !known.Contains(window) && window.IsVisible).ToList();
            if (opened.Count > 0 && seenAt < 0) seenAt = ticks;
            // A little time for a window to lay itself out and for a load from a missing computer to give up.
            if (opened.Count > 0 && ticks - seenAt >= 5)
            {
                int number = 0;
                foreach (var window in opened)
                {
                    Style(window);
                    window.UpdateLayout();
                    Save(window, pictures, folder, number++ == 0 ? name : $"{name}-{number}");
                    if (number == 1 && AlsoScrolledToEnd.Contains(name) && Descendants(window).OfType<ScrollViewer>().FirstOrDefault(scroll => scroll.ScrollableHeight > 0) is { } scrolled)
                    {
                        scrolled.ScrollToEnd();
                        window.UpdateLayout();
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                        Save(window, pictures, folder, name + "-modules");
                    }
                }
                foreach (var window in opened.AsEnumerable().Reverse())
                    try { window.Close(); } catch (InvalidOperationException) { }
                timer.Stop();
                frame.Continue = false;
            }
            else if (opened.Count == 0 && ticks >= 14)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        try { button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button)); }
        catch (Exception) { /* a handler that cannot work without real computers */ }
        if (frame.Continue) Dispatcher.PushFrame(frame);
        timer.Stop();
        Wait(100);
    }

    // ---- taking and saving a picture ---------------------------------------------------------------------

    static void Style(Window window)
    {
        window.SetResourceReference(Control.BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(Control.ForegroundProperty, "WindowForegroundBrush");
        window.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        window.UseLayoutRounding = true;
    }

    /// <summary>The window's content with a plain title bar drawn above it (a hidden window has no real one to photograph).</summary>
    static void Save(Window window, List<Picture> pictures, string folder, string slug)
    {
        if (!Wanted(slug)) return;
        if (window.Content is not FrameworkElement content || content.ActualWidth < 10 || content.ActualHeight < 10) return;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        const int bar = 30;
        // The picture is the whole layout slot of the content: Render draws an element where its layout put it, margin included, so a bitmap of the
        // element's own size would cut the margin off the right and the bottom (and with it the end of every line that runs to the edge).
        int width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
        int height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);

        var client = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
            context.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
        client.Render(backdrop);
        client.Render(content);

        var whole = new DrawingVisual();
        using (var context = whole.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xEF)), null, new Rect(0, 0, width, bar));
            var title = new FormattedText(window.Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 13, Brushes.Black, 1.0) { MaxTextWidth = Math.Max(20, width - 24), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            context.DrawText(title, new Point(12, (bar - title.Height) / 2));
            context.DrawImage(client, new Rect(0, bar, width, height));
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0xAD, 0xB5, 0xBD)), 1), new Rect(0.5, 0.5, width - 1, height + bar - 1));
        }
        var picture = new RenderTargetBitmap(width, height + bar, 96, 96, PixelFormats.Pbgra32);
        picture.Render(whole);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using (var file = File.Create(Path.Combine(folder, slug + ".png"))) encoder.Save(file);
        pictures.Add(new Picture(slug, window.Title));
    }

    static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static string Slug(string text)
    {
        var slug = new StringBuilder();
        foreach (var character in text.ToLowerInvariant())
            slug.Append(char.IsAsciiLetterOrDigit(character) ? character : '-');
        var result = slug.ToString();
        while (result.Contains("--")) result = result.Replace("--", "-");
        return result.Trim('-');
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

    // ---- the index page ------------------------------------------------------------------------------

    static void WriteIndex(string helpFolder, Dictionary<string, List<Picture>> taken)
    {
        var lithuanian = taken["lt"].ToDictionary(picture => picture.Slug, picture => picture.Title);
        var text = new StringBuilder();
        text.AppendLine("# Every window of Classroom Manager, in English and Lithuanian");
        text.AppendLine();
        text.AppendLine("These pictures are taken by a program, not by hand: `tests/ClassroomHost.Tests/HelpScreenshots.cs` opens the");
        text.AppendLine("teacher app with six made-up computers, presses every button and photographs what opens. Take them again");
        text.AppendLine("after the interface changed:");
        text.AppendLine();
        text.AppendLine("```powershell");
        text.AppendLine("$env:CLASSROOM_HELP_SCREENSHOTS = \"1\"");
        text.AppendLine("dotnet test tests\\ClassroomHost.Tests --filter \"FullyQualifiedName~HelpScreenshots\"");
        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine("The computers in the pictures do not exist, so a window that reads from a computer (programs, processes,");
        text.AppendLine("files, updates) shows that it could not reach it instead of a list. Windows' own file and folder pickers");
        text.AppendLine("and plain yes/no questions are not photographed.");
        text.AppendLine();
        text.AppendLine($"{taken["en"].Count} pictures per language.");
        text.AppendLine();
        text.AppendLine("| Window | English | Lietuvių |");
        text.AppendLine("| --- | --- | --- |");
        foreach (var picture in taken["en"])
        {
            var title = picture.Title.Replace("|", "\\|");
            var other = lithuanian.TryGetValue(picture.Slug, out var translated) ? translated.Replace("|", "\\|") : "";
            text.AppendLine($"| {title}<br>{other} | [![{title}](screenshots/en/{picture.Slug}.png)](screenshots/en/{picture.Slug}.png) | " +
                            (other.Length > 0 ? $"[![{other}](screenshots/lt/{picture.Slug}.png)](screenshots/lt/{picture.Slug}.png) |" : "|"));
        }
        File.WriteAllText(Path.Combine(helpFolder, "screenshots.md"), text.ToString());
    }

    // ---- closing a file or folder picker of Windows, which is not one of the program's own windows -------

    delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    static void CloseSystemDialogs()
    {
        uint me = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != me || !IsWindowVisible(window)) return true;
            var name = new StringBuilder(64);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() == "#32770") PostMessage(window, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }
}
