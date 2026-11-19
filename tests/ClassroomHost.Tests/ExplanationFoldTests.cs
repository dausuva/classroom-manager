// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>Tests that change the shared <see cref="UiPreferences"/>; they run alone, so windows built by other tests are not affected.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiPreferencesTests
{
    public const string Name = "UiPreferences";
}

/// <summary>A window's explanation shows in full the first few times the window opens, then folds behind an info icon.</summary>
[Collection(UiPreferencesTests.Name)]
public sealed class ExplanationFoldTests
{
    [Theory]
    [InlineData(0, false, Visibility.Visible)]
    [InlineData(UiPreferences.ExplanationShownTimes - 1, false, Visibility.Visible)]
    [InlineData(UiPreferences.ExplanationShownTimes, false, Visibility.Collapsed)]
    [InlineData(50, false, Visibility.Collapsed)]
    [InlineData(50, true, Visibility.Visible)]
    public void The_explanation_folds_after_the_first_openings_unless_always_shown(int openedBefore, bool alwaysShow, Visibility expected)
    {
        OnSta(() =>
        {
            var counted = new List<string>();
            using var _ = new Preferences(alwaysShow, name => { counted.Add(name); return openedBefore; });
            var (window, body, _) = Show("Explains the window.");
            try
            {
                Assert.Equal(expected, body.Visibility);
                Assert.Equal([nameof(Window)], counted);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Two_explanations_in_one_window_count_one_opening_and_fold_together()
    {
        OnSta(() =>
        {
            int calls = 0;
            using var _ = new Preferences(false, _ => { calls++; return 10; });
            var first = Ui.Explanation("First.");
            var second = Ui.Explanation("Second.");
            var window = Hidden(new StackPanel { Children = { first, second } });
            try
            {
                Assert.Equal(1, calls);
                Assert.Equal(Visibility.Collapsed, Body(first).Visibility);
                Assert.Equal(Visibility.Collapsed, Body(second).Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Without_a_counter_every_explanation_shows_in_full()
    {
        OnSta(() =>
        {
            using var _ = new Preferences(false, null);
            var (window, body, _) = Show("Shown.");
            try { Assert.Equal(Visibility.Visible, body.Visibility); }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void The_icon_carries_the_text_for_hover_and_keyboard_and_unfolds_it()
    {
        OnSta(() =>
        {
            using var _ = new Preferences(false, _ => 99);
            var (window, body, icon) = Show("Read me.");
            try
            {
                Assert.Equal("Read me.", ((TextBlock)icon.ToolTip).Text);
                Assert.True(ToolTipService.GetShowsToolTipOnKeyboardFocus(icon));
                Assert.Equal("Read me.", System.Windows.Automation.AutomationProperties.GetHelpText(icon));
                Assert.Equal(Visibility.Collapsed, body.Visibility);
                icon.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(Visibility.Visible, body.Visibility);
                icon.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(Visibility.Collapsed, body.Visibility);
            }
            finally { window.Close(); }
        });
    }

    static (Window Window, TextBlock Body, Button Icon) Show(string text)
    {
        var explanation = Ui.Explanation(text);
        var window = Hidden(explanation);
        var panel = (DockPanel)explanation;
        return (window, Body(explanation), panel.Children.OfType<Button>().Single());
    }

    static TextBlock Body(FrameworkElement explanation) => ((DockPanel)explanation).Children.OfType<TextBlock>().Single();

    /// <summary>A window that is never seen: off screen, not in the task bar, not activated; Loaded still runs.</summary>
    static Window Hidden(UIElement content)
    {
        var window = new Window
        {
            Content = content, Left = -20000, Top = -20000, Width = 300, Height = 200,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
        };
        window.Show();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        return window;
    }

    /// <summary>Sets the shared preferences for one test and puts the earlier values back.</summary>
    sealed class Preferences : IDisposable
    {
        readonly bool _always = UiPreferences.Instance.AlwaysShowExplanations;
        readonly Func<string, int>? _count = UiPreferences.Instance.CountOpening;

        public Preferences(bool alwaysShow, Func<string, int>? count)
        {
            UiPreferences.Instance.AlwaysShowExplanations = alwaysShow;
            UiPreferences.Instance.CountOpening = count;
        }

        public void Dispose()
        {
            UiPreferences.Instance.AlwaysShowExplanations = _always;
            UiPreferences.Instance.CountOpening = _count;
        }
    }

    static void OnSta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
