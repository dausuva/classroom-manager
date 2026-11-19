// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// Shows the Host in the chosen language. The program's own texts are English; every control is
/// translated where it appears on screen: when it is loaded, and again whenever its text changes.
/// So a window needs no code of its own to be translated, and the texts the program compares
/// (button names, column names, list choices) stay English underneath.
///
/// Text a person typed or that names their things (a computer, a file, a user) is not a program
/// text: input boxes are never touched, and table columns that hold such names are left alone.
/// </summary>
public static class UiTranslator
{
    /// <summary>Where a user's own translation files live: one PO file per language.</summary>
    public static string LanguagesFolder => Path.Combine(HostSettings.DataFolder, "languages");

    /// <summary>Table columns whose cells are names and addresses, never program text.</summary>
    static readonly HashSet<string> DataColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Name", "Computer", "Computers", "User", "Address", "Room", "MAC", "Key", "File", "Path", "Folder", "Program", "Publisher",
        "Title", "Window", "Process", "Domain", "Website", "Account", "Value", "Command", "Version", "Destination", "Fingerprint", "Target",
    };

    static bool _registered;

    /// <summary>Chooses the language and starts translating every window that opens from now on. Safe to call again after the setting changed.</summary>
    public static Translation Initialize(string? languageSetting)
    {
        var translation = Loc.Use(languageSetting, LanguagesFolder);
        if (!_registered)
        {
            _registered = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) => { if (sender is Window window) Attach(window); }));
        }
        return translation;
    }

    static bool Active => Loc.Current.Count > 0;

    // Opt-in typed presentation is already translated. Do not pattern-match its labels or data-bearing values a second time.
    public static readonly DependencyProperty PreserveTextProperty = DependencyProperty.RegisterAttached(
        "PreserveText", typeof(bool), typeof(UiTranslator), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static void SetPreserveText(DependencyObject element, bool value) => element.SetValue(PreserveTextProperty, value);
    public static bool GetPreserveText(DependencyObject element) => (bool)element.GetValue(PreserveTextProperty);

    /// <summary>The English text behind a text on screen: what the program's own comparisons need.</summary>
    public static string Source(string? shown) => Loc.Current.Original(shown);

    static readonly DependencyProperty EnglishTitleProperty =
        DependencyProperty.RegisterAttached("EnglishTitle", typeof(string), typeof(UiTranslator));

    /// <summary>
    /// The English title of a table column. It is remembered when the title is translated, because two
    /// titles can translate to the same text ("State" and "Status" are both "Būsena") and the English
    /// cannot be told from the translation then.
    /// </summary>
    public static string ColumnName(DataGridColumn column) =>
        column.GetValue(EnglishTitleProperty) as string ?? (column.Header is string title ? Source(title) : "");

    /// <summary>Typed/core header for a preserved grid. Keep persistence identity independent of translated-label collisions.</summary>
    public static void SetColumnHeader(DataGridColumn column, string english)
    {
        column.SetValue(EnglishTitleProperty, english);
        column.Header = Loc.T(english);
    }

    /// <summary>
    /// Translates a window now and keeps it translated: after every change of its layout (a text
    /// changed, a tab or a list opened, rows arrived) it is gone through again once the program is
    /// idle. Going through an already translated window changes nothing, so this settles at once.
    /// </summary>
    public static void Attach(Window window)
    {
        if (!Active) return;
        TranslateTree(window);
        bool pending = false;
        int restless = 0;
        void Again(object? sender, EventArgs e)
        {
            if (pending || !Active) return;
            pending = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                pending = false;
                // A window settles after one or two passes. One that keeps changing means a control puts its
                // English text back each time; stop rather than keep the processor busy for ever.
                restless = TranslateTree(window) ? restless + 1 : 0;
                if (restless > 40) window.LayoutUpdated -= Again;
            });
        }
        window.LayoutUpdated += Again;
        window.Closed += (_, _) => window.LayoutUpdated -= Again;
    }

    /// <summary>Translates everything under an element that is on screen now, including an open drop-down list or menu.</summary>
    [ThreadStatic] static bool _changed;

    /// <summary>Returns true when any text was changed.</summary>
    public static bool TranslateTree(DependencyObject root)
    {
        if (!Active) return false;
        _changed = false;
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            // What a person types, and a table cell that holds a name or an address, is theirs: not gone into.
            if (current is TextBoxBase or PasswordBox)
            {
                // The field's accessible metadata is ours; its text/password and visual children are not.
                TranslateAutomation((FrameworkElement)current);
                continue;
            }
            if (current is DataGridCell { Column.Header: string } cell && DataColumns.Contains(ColumnName(cell.Column))) continue;

            if (current is FrameworkElement element)
            {
                Translate(element);
                if (element.ContextMenu is { } menu) pending.Push(menu);
            }
            // A drop-down list and a menu live in a window of their own; their content is reached through their owner.
            if (current is Popup { Child: { } popupChild }) pending.Push(popupChild);
            if (current is MenuBase or MenuItem)
                foreach (var item in ((ItemsControl)current).Items.OfType<DependencyObject>()) pending.Push(item);

            if (current is Visual)
                for (int i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--) pending.Push(VisualTreeHelper.GetChild(current, i));
        }
        return _changed;
    }

    /// <summary>Translates the texts of one control.</summary>
    public static void Translate(FrameworkElement element)
    {
        if (!Active || GetPreserveText(element)) return;
        switch (element)
        {
            case Window window:
                Set(window, Window.TitleProperty, window.Title);
                break;
            case AccessText access:
                Set(access, AccessText.TextProperty, access.Text);
                break;
            case TextBlock block:
                // A block made of formatted runs has no single text.
                if (block.Inlines.Count <= 1) Set(block, TextBlock.TextProperty, block.Text);
                break;
            case HeaderedContentControl headered:
                if (headered.Header is string header) Set(headered, HeaderedContentControl.HeaderProperty, header);
                if (headered.Content is string body) Set(headered, ContentControl.ContentProperty, body);
                break;
            case HeaderedItemsControl items:
                if (items.Header is string itemHeader) Set(items, HeaderedItemsControl.HeaderProperty, itemHeader);
                break;
            case DataGridColumnHeader { Column: { Header: string columnTitle } column }:
                // The title is translated on the column itself, so the table measures the column with the
                // title it really shows. Code that needs the English name of a column asks Source().
                var shown = Loc.Current.Translate(columnTitle);
                if (shown == columnTitle) break;
                if (column.GetValue(EnglishTitleProperty) is null) column.SetValue(EnglishTitleProperty, columnTitle);
                column.Header = shown;
                _changed = true;
                break;
            case ContentControl content:
                // A list entry's own value is what the program reads back; the text drawn for it is translated instead.
                if (content.Content is string text && content is not (ListBoxItem or ComboBoxItem)) Set(content, ContentControl.ContentProperty, text);
                break;
        }
        if (element.ToolTip is string tip) Set(element, FrameworkElement.ToolTipProperty, tip);
        TranslateAutomation(element);
    }

    static void TranslateAutomation(FrameworkElement element)
    {
        if (GetPreserveText(element)) return;
        // What a screen reader says for the control: the same language as what is shown.
        if (System.Windows.Automation.AutomationProperties.GetName(element) is { Length: > 0 } spoken)
            Set(element, System.Windows.Automation.AutomationProperties.NameProperty, spoken);
        if (System.Windows.Automation.AutomationProperties.GetHelpText(element) is { Length: > 0 } help)
            Set(element, System.Windows.Automation.AutomationProperties.HelpTextProperty, help);
    }

    static void Set(FrameworkElement element, DependencyProperty property, string text)
    {
        var translated = Loc.Current.Translate(text);
        // SetCurrentValue keeps a binding or a style in place: the next English value arrives through it and is translated again.
        if (ReferenceEquals(translated, text) || translated == text) return;
        element.SetCurrentValue(property, translated);
        _changed = true;
    }
}

/// <summary>
/// Switches for the program that takes the pictures of every window for the help pages
/// (tests\ClassroomHost.Tests\HelpScreenshots.cs). Nothing sets them in normal use.
/// </summary>
public static class UiHooks
{
    /// <summary>The main window opens without asking for a key, scanning the network, refreshing or running schedules.</summary>
    public static bool QuietStart { get; set; }

    /// <summary>Answers a message box instead of showing it: given its text, caption and buttons.</summary>
    public static Func<string, string, MessageBoxButton, MessageBoxResult>? MessageBox { get; set; }
}

/// <summary>
/// The message box of the Host: the same calls as Windows' own, with the text and the caption
/// shown in the chosen language. Being in the Host's namespace it is the one every window uses.
/// </summary>
static class MessageBox
{
    static string T(string text) => Loc.T(text);

    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        if (UiHooks.MessageBox is { } answer) return answer(T(text), T(caption), button);
        return owner is null
            ? System.Windows.MessageBox.Show(T(text), T(caption), button, icon, defaultResult)
            : System.Windows.MessageBox.Show(owner, T(text), T(caption), button, icon, defaultResult);
    }

    public static MessageBoxResult Show(string messageBoxText) => Show(null, messageBoxText, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(string messageBoxText, string caption) => Show(null, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) =>
        Show(null, messageBoxText, caption, button, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        Show(null, messageBoxText, caption, button, icon, MessageBoxResult.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult) =>
        Show(null, messageBoxText, caption, button, icon, defaultResult);

    public static MessageBoxResult Show(Window? owner, string messageBoxText) =>
        Show(owner, messageBoxText, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption) =>
        Show(owner, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button) =>
        Show(owner, messageBoxText, caption, button, MessageBoxImage.None, MessageBoxResult.None);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        Show(owner, messageBoxText, caption, button, icon, MessageBoxResult.None);
}
