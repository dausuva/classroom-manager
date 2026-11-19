// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClassroomManager.Host;

public sealed record FeedbackContext(
    string ContextId,
    string WindowTitle,
    string WindowType,
    string TabTitle);

/// <summary>Adds a context-aware feedback shortcut without altering a window's visual layout.</summary>
public static class FeedbackUi
{
    internal static IEnumerable<string> Secrets(Window window)
    {
        for (Window? owner = window; owner is not null; owner = owner.Owner)
            if (owner is IFeedbackSecretSource source) return source.FeedbackSecrets;
        return Application.Current?.MainWindow is IFeedbackSecretSource main ? main.FeedbackSecrets : [];
    }

    static readonly Type[] ExcludedWindowTypes =
    [
        typeof(FeedbackCaptureWindow),
        typeof(FeedbackManagerWindow),
        typeof(AboutWindow),
        typeof(OpenSourceLicensesWindow),
    ];

    static readonly DependencyProperty IsAttachedProperty = DependencyProperty.RegisterAttached(
        "IsAttached", typeof(bool), typeof(FeedbackUi), new PropertyMetadata(false));

    public static void Attach(Window window)
    {
        if (!ShouldAttachTo(window.GetType()) || (bool)window.GetValue(IsAttachedProperty)) return;
        window.SetValue(IsAttachedProperty, true);
        window.PreviewKeyDown += (_, e) =>
        {
            if (!IsFeedbackShortcut(e.Key, Keyboard.Modifiers)) return;
            e.Handled = true;
            FeedbackCaptureWindow.Open(window, Capture(window));
        };
    }

    public static bool IsFeedbackShortcut(Key key, ModifierKeys modifiers) =>
        key == Key.F && modifiers == (ModifierKeys.Control | ModifierKeys.Shift);

    public static bool ShouldAttachTo(Type windowType) =>
        !ExcludedWindowTypes.Any(excluded => excluded.IsAssignableFrom(windowType));

    public static FeedbackContext Capture(Window window)
    {
        var tab = FindSelectedTab(window);
        var tabTitle = CleanHeader(tab?.Header);
        var windowType = window.GetType().Name;
        var contextId = tabTitle.Length == 0
            ? windowType
            : $"{windowType}/{Slug(tabTitle)}";
        return new FeedbackContext(contextId, window.Title, windowType, tabTitle);
    }

    static TabItem? FindSelectedTab(DependencyObject root)
    {
        if (root is TabControl tabs && tabs.SelectedItem is TabItem selected && selected.IsVisible)
            return selected;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindSelectedTab(VisualTreeHelper.GetChild(root, i));
            if (result is not null) return result;
        }
        return null;
    }

    static string CleanHeader(object? header) => header switch
    {
        string value => value.Replace("_", "").Trim(),
        AccessText access => access.Text.Replace("_", "").Trim(),
        TextBlock text => text.Text.Replace("_", "").Trim(),
        _ => header?.ToString()?.Replace("_", "").Trim() ?? "",
    };

    static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>Chooses where the feedback window opens so it covers as little of the described window as possible.</summary>
public static class FeedbackPlacement
{
    const double Inset = 24;

    /// <summary>Top-left corner that tucks the window into the described window's bottom-right corner, kept on screen.</summary>
    public static Point BottomRight(Rect described, Size window, Rect screen)
    {
        var left = described.Right - window.Width - Inset;
        var top = described.Bottom - window.Height - Inset;
        return new Point(
            Math.Max(screen.Left, Math.Min(left, screen.Right - window.Width)),
            Math.Max(screen.Top, Math.Min(top, screen.Bottom - window.Height)));
    }
}

/// <summary>
/// Modeless, always-on-top feedback form: the user can keep working in the window being described,
/// including modal dialogs opened after it, while writing the note.
/// </summary>
public sealed class FeedbackCaptureWindow : Window
{
    const int WmEnable = 0x000A;
    static FeedbackCaptureWindow? s_open;
    bool _showingOwnMessage;
    readonly FeedbackContext _context;
    readonly FeedbackStore _store = new();
    readonly ProductSupportSettings _support = ProductSupportSettings.Load();
    readonly TextBox _feedback = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 210,
    };
    readonly TextBlock _saveStatus = Ui.StatusText(new Thickness(0, 8, 0, 0));

    /// <summary>Shows the feedback form for <paramref name="described"/>, or brings the open one forward with its text intact.</summary>
    public static void Open(Window described, FeedbackContext context)
    {
        if (s_open is not null)
        {
            if (s_open.WindowState == WindowState.Minimized) s_open.WindowState = WindowState.Normal;
            s_open.Activate();
            return;
        }
        var window = new FeedbackCaptureWindow(described, context);
        s_open = window;
        window.Closed += (_, _) => s_open = null;
        window.Show();
    }

    FeedbackCaptureWindow(Window described, FeedbackContext context)
    {
        _context = context;
        // Owned by the main window rather than the described one: closing a described dialog must not discard the note.
        var main = Application.Current?.MainWindow;
        Owner = main is not null && main.IsLoaded ? main : described;
        Topmost = true;
        ShowInTaskbar = false;
        Title = "Send feedback";
        Width = 480;
        Height = 430;
        MinWidth = 460;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var position = FeedbackPlacement.BottomRight(ScreenRect(described), new Size(Width, Height),
            new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
        Left = position.X;
        Top = position.Y;

        var heading = new TextBlock
        {
            Text = "Tell us what could work better",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };

        var details = new TextBlock
        {
            Text = $"Screen ID: {context.ContextId}\n" +
                   $"Window: {context.WindowTitle}\nVisible tab: {(context.TabTitle.Length > 0 ? context.TabTitle : "Not applicable")}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 6, 8, 8),
        };
        details.SetResourceReference(StyleProperty, "StatusTextStyle");
        var contextDetails = new Expander
        {
            Header = "Included app context",
            Content = details,
            IsExpanded = false,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var note = new TextBlock
        {
            Text = $"Describe what happened, what you expected, and how to reproduce it. The current screen and app version are included automatically. You can save it locally or open an email to {_support.SupportEmail}. Do not include passwords, shared keys, or student personal data.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        var label = Ui.Label("Your _feedback", _feedback, new Thickness(0, 0, 0, 4));
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var save = new Button { Content = "_Save feedback", Tag = FluentIcons.Save, MinWidth = 130, IsDefault = true };
        save.Click += Save_Click;
        var send = new Button { Content = "_Email support now", Tag = FluentIcons.Mail, MinWidth = 150, Margin = new Thickness(8, 0, 0, 0) };
        send.Click += Send_Click;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(save);
        buttons.Children.Add(send);

        var root = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(contextDetails, Dock.Top);
        DockPanel.SetDock(label, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(heading);
        root.Children.Add(note);
        root.Children.Add(contextDetails);
        root.Children.Add(label);
        root.Children.Add(buttons);
        DockPanel.SetDock(_saveStatus, Dock.Bottom);
        root.Children.Add(_saveStatus);
        root.Children.Add(_feedback);
        Content = root;

        Loaded += (_, _) => _feedback.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    /// <summary>The described window's bounds in device-independent units, correct when it is maximized or on a scaled display.</summary>
    static Rect ScreenRect(Window window)
    {
        var target = PresentationSource.FromVisual(window)?.CompositionTarget;
        if (target is null) return new Rect(SystemParameters.WorkArea.Location, SystemParameters.WorkArea.Size);
        var topLeft = target.TransformFromDevice.Transform(window.PointToScreen(new Point(0, 0)));
        return new Rect(topLeft, new Size(window.ActualWidth, window.ActualHeight));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // A modal dialog opened elsewhere in the Host disables every other window. Undo that for this
        // window so feedback stays usable, except while one of its own message boxes is showing.
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(
            (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == WmEnable && wParam == IntPtr.Zero && !_showingOwnMessage) EnableWindow(hwnd, true);
                return IntPtr.Zero;
            });
    }

    [DllImport("user32.dll")]
    static extern bool EnableWindow(IntPtr hwnd, bool enable);

    void ShowMessage(string text, string caption, MessageBoxImage image)
    {
        _showingOwnMessage = true;
        try { MessageBox.Show(this, text, caption, MessageBoxButton.OK, image); }
        finally { _showingOwnMessage = false; }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var entry = CreateEntry(FeedbackStatuses.Saved);
        if (entry is null) return;
        try
        {
            _saveStatus.Text = "Saving...";
            _store.Upsert(entry);
            _saveStatus.Text = "Saved.";
            Close();
        }
        catch (Exception ex)
        {
            ShowMessage("Feedback could not be saved. Your text remains in this window.\n\n" + ex.Message,
                "Could not save feedback", MessageBoxImage.Error);
        }
    }

    void Send_Click(object sender, RoutedEventArgs e)
    {
        var entry = CreateEntry(FeedbackStatuses.Saved);
        if (entry is null) return;
        try
        {
            _store.Upsert(entry);
            var report = FeedbackReport.Format(new[] { entry }, _support, knownSecrets: FeedbackUi.Secrets(this));
            Clipboard.SetText(report);
            FeedbackSharing.OpenEmail(_support, entry, report);
            entry.Status = FeedbackStatuses.ShareOpened;
            _store.Upsert(entry);
            ShowMessage(
                "Your email app was opened and the full feedback report was copied to the clipboard. Review the message and send it from your email app.",
                "Feedback ready to send", MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            ShowMessage(
                "The feedback was saved, but the email app could not be opened. Use Settings > Review saved feedback to copy or share it later.\n\n" + ex.Message,
                "Feedback saved for later", MessageBoxImage.Warning);
        }
    }

    FeedbackEntry? CreateEntry(string status)
    {
        var text = _feedback.Text.Trim();
        if (text.Length == 0)
        {
            ShowMessage("Enter feedback before saving or sending it.", "Feedback", MessageBoxImage.Information);
            _feedback.Focus();
            return null;
        }
        if (text.Length > 20_000)
        {
            ShowMessage("Feedback is limited to 20,000 characters. Shorten the text and try again.",
                "Feedback is too long", MessageBoxImage.Information);
            return null;
        }
        return FeedbackPrivacy.Redact(new FeedbackEntry
        {
            Status = status,
            ContextId = _context.ContextId,
            WindowTitle = _context.WindowTitle,
            WindowType = _context.WindowType,
            TabTitle = _context.TabTitle,
            HostVersion = HostBuildInfo.Version,
            Text = text,
        }, FeedbackUi.Secrets(this));
    }
}

public static class FeedbackSharing
{
    public static void OpenEmail(ProductSupportSettings support, FeedbackEntry entry, string report)
    {
        var subject = $"Classroom Manager feedback {entry.Id[..8]} - {entry.ContextId}";
        var excerpt = report.Length <= 3500 ? report : report[..3500] +
            "\n\n[The full report was copied to the clipboard. Paste it into this message if needed.]";
        var uri = $"mailto:{support.SupportEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(excerpt)}";
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }
}

public sealed class FeedbackManagerWindow : Window
{
    readonly FeedbackStore _store = new();
    readonly ProductSupportSettings _support = ProductSupportSettings.Load();
    readonly TextBox _report = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"),
    };
    readonly TextBlock _status = Ui.StatusText();
    List<FeedbackEntry> _entries = new();

    public FeedbackManagerWindow(Window owner)
    {
        Owner = owner;
        Title = "Feedback and support";
        Width = 850;
        Height = 620;
        MinWidth = 600;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = $"Support: {_support.SupportEmail}",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var note = Ui.Explanation(
            "Saved feedback stays on this Windows account until you clean it up. Sharing copies the complete report and opens the default email app; review and send it there.",
            new Thickness(0, 0, 0, 10));

        var refresh = Button("_Refresh", "Refresh — reload feedback from disk", (_, _) => LoadReport());
        var copy = Button("_Copy all", "Copy all — copy the complete feedback report", Copy_Click);
        var share = Button("_Share gathered feedback", "Share gathered feedback — copy the report and open an email draft", Share_Click);
        var export = Button("_Export dated TXT...", "Export — save the complete report as a dated text file", Export_Click);
        var clean = Button("_Clean up shared...", "Clean up shared — remove entries whose email draft was opened", Clean_Click);
        var clear = Button("C_lear all...", "Clear all — permanently remove every gathered feedback entry", Clear_Click);
        var close = Button("_Close", "Close — return to the Host", (_, _) => Close());
        close.IsCancel = true;

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in new[] { refresh, copy, export, share, clean, clear, close }) buttons.Children.Add(button);

        var root = new DockPanel { Margin = new Thickness(14), LastChildFill = true };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(heading);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        root.Children.Add(_report);
        Content = root;

        Loaded += (_, _) => LoadReport();
    }

    static Button Button(string content, string tooltip, RoutedEventHandler click)
    {
        var button = new Button { Content = content, ToolTip = tooltip, MinWidth = 92, Margin = new Thickness(0, 0, 8, 8) };
        button.Click += click;
        return button;
    }

    void LoadReport()
    {
        try
        {
            _entries = _store.Load().Select(entry => FeedbackPrivacy.Redact(entry, FeedbackUi.Secrets(this))).ToList();
            _report.Text = FeedbackReport.Format(_entries, _support, knownSecrets: FeedbackUi.Secrets(this));
            _status.Text = _entries.Count == 0
                ? "No feedback has been gathered yet."
                : $"Loaded {_entries.Count} feedback item(s): {_entries.Count(x => x.Status == FeedbackStatuses.Saved)} saved for later, {_entries.Count(x => x.Status == FeedbackStatuses.ShareOpened)} shared draft(s) opened.";
        }
        catch (Exception ex)
        {
            _entries.Clear();
            _report.Text = "";
            _status.Text = "Feedback could not be loaded: " + ex.Message;
        }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0) { MessageBox.Show(this, "There is no feedback to copy."); return; }
        Clipboard.SetText(_report.Text);
        _status.Text = "Complete feedback report copied to the clipboard.";
    }

    void Share_Click(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0) { MessageBox.Show(this, "There is no feedback to share."); return; }
        try
        {
            Clipboard.SetText(_report.Text);
            var representative = _entries[0];
            FeedbackSharing.OpenEmail(_support, representative, _report.Text);
            foreach (var entry in _entries)
            {
                entry.Status = FeedbackStatuses.ShareOpened;
                _store.Upsert(entry);
            }
            LoadReport();
            MessageBox.Show(this,
                "Your email app was opened and the complete report was copied. Review the message, paste the full report if needed, and send it from your email app.",
                "Feedback ready to share", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open the email app. The gathered feedback remains saved.\n\n" + ex.Message,
                "Could not share feedback", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0) { MessageBox.Show(this, "There is no feedback to export."); return; }
        var dialog = new SaveFileDialog
        {
            Title = "Export feedback report",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"Dausuva-Classroom-Manager-feedback-{DateTime.Now:yyyy-MM-dd}.txt",
            DefaultExt = ".txt",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _report.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _status.Text = $"Feedback exported to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not export the feedback report.\n\n" + ex.Message,
                "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void Clean_Click(object sender, RoutedEventArgs e)
    {
        var count = _entries.Count(x => x.Status == FeedbackStatuses.ShareOpened);
        if (count == 0) { MessageBox.Show(this, "There are no shared feedback entries to clean up."); return; }
        if (MessageBox.Show(this, $"Remove {count} feedback item(s) whose email draft was opened?\n\nSaved-for-later drafts will remain.",
                "Clean up shared feedback", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _store.RemoveShared();
        LoadReport();
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0) return;
        if (MessageBox.Show(this, $"Permanently remove all {_entries.Count} gathered feedback item(s)?",
                "Clear all feedback", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _store.Clear();
        LoadReport();
    }
}
