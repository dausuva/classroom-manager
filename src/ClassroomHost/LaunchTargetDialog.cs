// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed record LaunchHistoryChoice(string Target, string Arguments, bool Saved)
{
    public string Display => $"{(Saved ? "Saved" : "Recent")}: {Target}";
}

/// <summary>The choices an Open website prompt adds: a maximized window and a browser (a BrowserCatalog id, empty for the default).</summary>
public sealed record WebsiteChoices(bool Maximized, string BrowserId);

/// <summary>Editable launch prompt backed by pinned and recent Host history.</summary>
public sealed class LaunchTargetDialog : Window
{
    readonly TextBox _target = new() { Margin = new Thickness(0, 0, 0, 8) };
    readonly ComboBox _history = new() { DisplayMemberPath = nameof(LaunchHistoryChoice.Display), Margin = new Thickness(0, 0, 0, 8) };
    readonly TextBox? _arguments;
    readonly CheckBox _save = new() { Content = "_Save for later", Margin = new Thickness(0, 0, 0, 8) };
    readonly CheckBox _maximized = new() { Content = "Open _maximized", Margin = new Thickness(0, 0, 0, 8) };
    readonly ComboBox _browser = new() { DisplayMemberPath = nameof(BrowserChoice.Name), Margin = new Thickness(0, 0, 0, 8) };

    sealed record BrowserChoice(string Id, string Name);

    /// <summary>The browser and window choices, when the prompt was asked to offer them.</summary>
    public WebsiteChoices? Choices { get; private set; }

    public string Target => _target.Text.Trim();
    public string Arguments => _arguments?.Text.Trim() ?? "";
    public bool SaveForLater => _save.IsChecked == true;

    public LaunchTargetDialog(string title, string targetLabel, string defaultTarget,
        IReadOnlyList<LaunchHistoryChoice> history, bool includeArguments, string note, Window owner,
        WebsiteChoices? websiteChoices = null)
    {
        Title = title;
        Owner = owner;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _target.Text = defaultTarget;
        _arguments = includeArguments ? new TextBox { Margin = new Thickness(0, 0, 0, 8) } : null;
        _history.ItemsSource = history;
        _history.SelectionChanged += (_, _) =>
        {
            if (_history.SelectedItem is not LaunchHistoryChoice choice) return;
            _target.Text = choice.Target;
            if (_arguments is not null) _arguments.Text = choice.Arguments;
            _save.IsChecked = choice.Saved;
            _target.Focus();
            _target.CaretIndex = _target.Text.Length;
        };

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new Label { Content = targetLabel, Target = _target, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 2) });
        panel.Children.Add(_target);
        if (history.Count > 0)
        {
            panel.Children.Add(new Label { Content = "Saved and _recent", Target = _history, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 2) });
            panel.Children.Add(_history);
        }
        else
        {
            panel.Children.Add(Ui.MutedText("No saved or recent entries yet. Type a new entry above.", new Thickness(0, 0, 0, 8)));
        }
        if (_arguments is not null)
        {
            panel.Children.Add(new Label { Content = "_Arguments (optional)", Target = _arguments, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 2) });
            panel.Children.Add(_arguments);
        }
        if (websiteChoices is not null)
        {
            var browsers = new List<BrowserChoice> { new("", "Default browser") };
            browsers.AddRange(BrowserCatalog.All.Select(known => new BrowserChoice(known.Id, known.Name)));
            _browser.ItemsSource = browsers;
            _browser.SelectedItem = browsers.FirstOrDefault(choice => choice.Id.Equals(websiteChoices.BrowserId, StringComparison.OrdinalIgnoreCase)) ?? browsers[0];
            _maximized.IsChecked = websiteChoices.Maximized;
            panel.Children.Add(new Label { Content = "_Browser", Target = _browser, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 2) });
            panel.Children.Add(_browser);
            panel.Children.Add(_maximized);
            Choices = websiteChoices;
        }
        panel.Children.Add(_save);
        panel.Children.Add(Ui.MutedText(note, new Thickness(0, 0, 0, 10)));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var open = new Button { Content = "_Open", MinWidth = 88, IsDefault = true };
        var cancel = new Button { Content = "_Cancel", MinWidth = 88, IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        open.Click += (_, _) =>
        {
            if (Choices is not null)
                Choices = new WebsiteChoices(_maximized.IsChecked == true, (_browser.SelectedItem as BrowserChoice)?.Id ?? "");
            DialogResult = true;
        };
        buttons.Children.Add(open);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            _target.Focus();
            Keyboard.Focus(_target);
            _target.SelectAll();
        };
    }
}
