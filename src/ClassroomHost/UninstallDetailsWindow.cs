// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;

namespace ClassroomManager.Host;

public sealed class UninstallDetailsWindow : Window
{
    public UninstallDetailsWindow(Window owner, ProgramEntry entry)
    {
        Owner = owner;
        Title = $"Uninstall details - {entry.Name}";
        Width = 720;
        Height = 480;
        MinWidth = 520;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var details = new TextBox
        {
            Text = BuildDetails(entry),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        };

        var close = new Button { Content = "_Close", IsCancel = true, MinWidth = 88 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        if (entry.SuggestedSilentArgs.Length > 0)
        {
            var use = new Button { Content = "_Use suggested switches", IsDefault = true, MinWidth = 150, Margin = new Thickness(0, 0, 8, 0) };
            use.Click += (_, _) => DialogResult = true;
            buttons.Children.Add(use);
        }
        buttons.Children.Add(close);

        var note = Ui.Explanation("These values come from the selected computer's uninstall registry entry. Select text and press Ctrl+C to copy it.", new Thickness(0, 0, 0, 8));
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(buttons);
        root.Children.Add(details);
        Content = root;
    }

    static string BuildDetails(ProgramEntry entry) =>
        $"Program: {entry.Name}\r\n" +
        $"Version: {entry.Version}\r\n" +
        $"Publisher: {entry.Publisher}\r\n\r\n" +
        $"Detection: {entry.SilentText}\r\n" +
        $"Explanation: {entry.SilentNote}\r\n\r\n" +
        $"UninstallString:\r\n{Value(entry.UninstallString)}\r\n\r\n" +
        $"QuietUninstallString:\r\n{Value(entry.QuietUninstallString)}\r\n\r\n" +
        $"Suggested switches (candidate only):\r\n{Value(entry.SuggestedSilentArgs)}";

    static string Value(string value) => value.Length == 0 ? "(not provided)" : value;
}
