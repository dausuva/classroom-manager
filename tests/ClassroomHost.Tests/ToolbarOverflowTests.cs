// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ToolbarOverflowTests
{
    [Fact]
    public void Overflow_keeps_fields_ids_states_and_clicks_and_restores_the_users_order()
    {
        OnSta(() =>
        {
            var top = new WrapPanel();
            var overflow = new WrapPanel();
            var field = new ComboBox { Width = 80, ItemsSource = new[] { "Room A", "Room B" }, SelectedIndex = 1 };
            var commands = new[] { "Zulu", "Alpha", "Middle", "Bravo" }.Select(text => new Button { Content = text, Width = 100 }).ToArray();
            CommandState.SetId(commands[2], "custom:middle");
            commands[3].IsEnabled = false;
            int clicks = 0;
            commands[2].Click += (_, _) => clicks++;
            UIElement[] original = [commands[0], field, commands[1], commands[2], commands[3]];
            foreach (var element in original) top.Children.Add(element);
            var responsive = new ToolbarOverflow(top, overflow, button => (string)button.Content);
            Assert.True(responsive.Reflow(300));
            Assert.Equal(new UIElement[] { commands[0], field, commands[1] }, top.Children.Cast<UIElement>());
            Assert.Equal(new UIElement[] { commands[3], commands[2] }, overflow.Children.Cast<UIElement>());
            Assert.False(responsive.Reflow(300));
            Assert.Equal(1, field.SelectedIndex);
            Assert.False(commands[3].IsEnabled);
            Assert.Equal("custom:middle", CommandState.GetId(commands[2]));
            commands[2].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, clicks);
            Assert.True(responsive.Reflow(1000));
            Assert.Equal(original, top.Children.Cast<UIElement>());
            Assert.Empty(overflow.Children);

            commands[0].Visibility = Visibility.Collapsed;
            responsive.Reflow(300);
            Assert.DoesNotContain(commands[0], overflow.Children.Cast<UIElement>());
            Assert.Equal(Visibility.Collapsed, commands[0].Visibility);
            responsive.Restore();
            top.Children.Remove(commands[3]);
            top.Children.Insert(0, commands[3]); // A new saved arrangement becomes the next priority order.
            var rearranged = top.Children.Cast<UIElement>().ToArray();
            responsive.Capture();
            responsive.Reflow(180);
            responsive.Restore();
            Assert.Equal(rearranged, top.Children.Cast<UIElement>());
        });
    }

    [Fact]
    public void Larger_text_and_narrower_width_leave_the_bar_with_one_fitting_row()
    {
        OnSta(() =>
        {
            var top = new WrapPanel();
            var overflow = new WrapPanel();
            foreach (var text in new[] { "First action", "Second action", "Third action", "Fourth action" })
                top.Children.Add(new Button { Content = text, FontSize = 14 });
            var buttons = top.Children.OfType<Button>().ToArray();
            var responsive = new ToolbarOverflow(top, overflow, button => (string)button.Content);
            responsive.Reflow(300);
            var initial = top.Children.Count;
            foreach (var button in buttons) button.FontSize = 28;
            responsive.Reflow(300);
            Assert.True(top.Children.Count <= initial);
            Assert.True(top.Children.OfType<UIElement>().Sum(element => element.DesiredSize.Width) <= 300.5);
            responsive.Reflow(150);
            Assert.True(top.Children.OfType<UIElement>().Sum(element => element.DesiredSize.Width) <= 150.5);
            responsive.Reflow(2000);
            Assert.Equal(buttons, top.Children.OfType<Button>());
        });
    }

    static void OnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join(); failure?.Throw();
    }
}
