// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClassroomManager.Host;

public partial class App : Application
{
    /// <summary>First argument that makes the Host run a scripted command instead of opening its window.</summary>
    public const string CliSwitch = "--cli";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before anything reads a setting: --data-folder, CLASSROOM_DATA_FOLDER or a ClassroomHost.portable file beside the program may move the Host's files.
        string[] args;
        try { args = HostDataLocation.Apply(e.Args); }
        catch (InvalidOperationException ex)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // A scripted run (--cli) gets the reason as text on standard error; a person starting the window gets a message.
            if (e.Args.Any(arg => arg.Equals(CliSwitch, StringComparison.OrdinalIgnoreCase)))
            {
                AttachCallerConsole();
                Console.Error.WriteLine(ex.Message);
                Console.Error.Flush();
            }
            else MessageBox.Show($"The Host did not start. {ex.Message}", "Classroom Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }

        if (args.Length > 0 && args[0] == "--startup-probe")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(args.Length == 2 ? HostStartupProbe.Run(args[1]) : 2);
            return;
        }

        if (args.Length > 0 && args[0].Equals(CliSwitch, StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(RunCommandLine(args.Skip(1).ToArray()));
            return;
        }

        // Every window in the app is a Window subclass, which an implicit Window style would not
        // reach. A class handler on Loaded fires for all of them (including ones opened later and
        // FormDialog prompts) and hooks their background and text colour to the theme brushes,
        // before the window is first drawn.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(ApplyThemeToWindow));
        TableSorting.Initialize();

        // Apply the saved theme and button-display mode before the first window is created.
        var settings = HostSettings.Load();
        // One generation of settings and keys per start (when they changed), so a bad write is never final.
        try { HostBackups.Snapshot(HostSettings.PathOnDisk, HostBackups.Start); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        CrashLog.Initialize(settings.CrashLoggingEnabled);
        UiTranslator.Initialize(settings.Language);
        UiPreferences.Instance.Display = Enum.TryParse<ButtonDisplayMode>(settings.ButtonDisplay, out var mode)
            ? mode : ButtonDisplayMode.IconsAndNames;
        ThemeManager.Initialize(Enum.TryParse<AppTheme>(settings.Theme, out var theme) ? theme : AppTheme.Light);

        // The Apps entry of a copy installed from inside the program runs this (HostSelfInstall).
        if (args.Length > 0 && args[0].Equals(HostSelfInstall.UninstallSwitch, StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(HostSelfInstall.RunUninstall());
            return;
        }

        new MainWindow().Show();
    }

    /// <summary>
    /// Runs <see cref="HostCli"/> in this process so scripted control ships inside the one signed Host
    /// executable (see ClassroomCli.cmd). A windowed program starts without a console, so it borrows
    /// the caller's; output the caller redirected to a file or pipe keeps going there.
    /// </summary>
    static int RunCommandLine(string[] args)
    {
        AttachCallerConsole();
        // Off the UI thread: nothing pumps the dispatcher here, so awaits must not resume on it.
        var exitCode = Task.Run(() => HostCli.RunAsync(args, Console.Out, Console.Error)).GetAwaiter().GetResult();
        Console.Out.Flush();
        Console.Error.Flush();
        return exitCode;
    }

    /// <summary>
    /// Attaching points both standard streams at the console, which would silently discard output the caller redirected;
    /// remember any redirected stream and put it back afterwards.
    /// </summary>
    static void AttachCallerConsole()
    {
        var output = GetStdHandle(StdOutput);
        var error = GetStdHandle(StdError);
        var outputRedirected = GetFileType(output) != FileTypeUnknown;
        var errorRedirected = GetFileType(error) != FileTypeUnknown;
        if (!outputRedirected || !errorRedirected)
        {
            AttachConsole(AttachParentProcess);
            if (outputRedirected) SetStdHandle(StdOutput, output);
            if (errorRedirected) SetStdHandle(StdError, error);
        }
    }

    const int AttachParentProcess = -1;
    const int StdOutput = -11;
    const int StdError = -12;
    const int FileTypeUnknown = 0;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr GetStdHandle(int handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool SetStdHandle(int handle, IntPtr value);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern int GetFileType(IntPtr handle);

    static void ApplyThemeToWindow(object sender, RoutedEventArgs e)
    {
        if (sender is not Window w) return;
        w.SetResourceReference(Control.BackgroundProperty, "WindowBackgroundBrush");
        w.SetResourceReference(Control.ForegroundProperty, "WindowForegroundBrush");
        w.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        w.UseLayoutRounding = true;
        w.SnapsToDevicePixels = true;
        WindowsTitleBar.Apply(w, ThemeManager.EffectiveTheme == AppTheme.Dark, SystemParameters.HighContrast);
        FeedbackUi.Attach(w);
        Ui.NameLists(w, w.Title);
        if (w is not ClassroomManager.Host.MainWindow) { Ui.CompactButtons(w); Ui.FitToTables(w); }
    }
}
