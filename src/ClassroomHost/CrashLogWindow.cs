// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;

namespace ClassroomManager.Host;

public sealed class CrashLogWindow : Window
{
    readonly TextBox _log = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"),
    };
    readonly TextBlock _status = Ui.StatusText();

    public CrashLogWindow(Window owner)
    {
        Owner = owner;
        Title = "Host crash log";
        Width = 850;
        Height = 580;
        MinWidth = 600;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var refresh = Button("_Refresh", (_, _) => Refresh());
        var copy = Button("_Copy", (_, _) => { Clipboard.SetText(_log.Text); _status.Text = "Crash log copied to the clipboard."; });
        var clear = Button("_Clear log...", (_, _) => Clear());
        var close = Button("_Close", (_, _) => Close());
        close.IsCancel = true;
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in new[] { refresh, copy, clear, close }) buttons.Children.Add(button);
        var note = Ui.Explanation($"Local Host crash reports are stored at {CrashLog.Path}. Reports include exception details and Host version; they are not sent automatically.", new Thickness(0, 0, 0, 8));
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        root.Children.Add(_log);
        Content = root;
        Loaded += (_, _) => Refresh();
    }

    static Button Button(string content, RoutedEventHandler click)
    {
        var button = new Button { Content = content, MinWidth = 90, ToolTip = content.Replace("_", "") };
        button.Click += click;
        return button;
    }

    void Refresh()
    {
        _log.Text = CrashLog.Read();
        _status.Text = File.Exists(CrashLog.Path) ? $"Crash log size: {new FileInfo(CrashLog.Path).Length:N0} bytes." : "No crash log file exists.";
    }

    void Clear()
    {
        if (MessageBox.Show(this, "Clear all locally stored Host crash reports?", "Clear crash log",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try { CrashLog.Clear(); Refresh(); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
}
