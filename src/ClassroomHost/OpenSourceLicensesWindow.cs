// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClassroomManager.Host;

public sealed class OpenSourceLicensesWindow : Window
{
    readonly TextBox _text = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    public OpenSourceLicensesWindow(Window owner)
    {
        Owner = owner;
        Title = "Open-source licenses";
        Width = 820;
        Height = 620;
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var sections = new Dictionary<string, string>
        {
            ["Classroom Manager — Apache License 2.0"] = ReadResource("ClassroomManager.LICENSE"),
            ["Product notice"] = ReadResource("ClassroomManager.NOTICE"),
            ["Microsoft .NET and runtime libraries"] = ReadResource("ClassroomManager.THIRD-PARTY-NOTICES"),
        };
        var picker = new ComboBox
        {
            ItemsSource = sections.Keys,
            SelectedIndex = 0,
            Margin = new Thickness(0, 0, 0, 8),
        };
        System.Windows.Automation.AutomationProperties.SetName(picker, "License");
        System.Windows.Automation.AutomationProperties.SetName(_text, "License text");
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is string key) _text.Text = sections[key];
        };
        _text.Text = sections.First().Value;

        var copy = new Button { Content = "_Copy", ToolTip = "Copy — copy the displayed license text to the clipboard." };
        copy.Click += (_, _) => Clipboard.SetText(_text.Text);
        var close = new Button { Content = "_Close", IsDefault = true, IsCancel = true, MinWidth = 88 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(picker, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(picker);
        root.Children.Add(buttons);
        root.Children.Add(_text);
        Content = root;
    }

    static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return $"Embedded notice '{name}' is unavailable.";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
