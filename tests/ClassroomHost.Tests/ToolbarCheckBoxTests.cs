// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ToolbarCheckBoxTests
{
    [Fact]
    public void A_tick_box_in_a_row_of_buttons_looks_like_a_button_and_one_in_a_form_stays_plain()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var inRow = new CheckBox { Content = "Include optional/driver updates" };
                Ui.Spaced(new WrapPanel { Children = { new Button { Content = "_Check" }, inRow } });
                var inForm = new CheckBox { Content = "Keep an activity log" };
                Ui.Spaced(new StackPanel { Children = { new TextBox(), inForm } });

                Assert.NotEqual(DependencyProperty.UnsetValue, inRow.ReadLocalValue(FrameworkElement.StyleProperty));
                Assert.Equal(DependencyProperty.UnsetValue, inForm.ReadLocalValue(FrameworkElement.StyleProperty));
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
