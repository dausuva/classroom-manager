// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Xunit;
using ClassroomManager.Host;

namespace ClassroomHost.Tests;

public sealed class FormDialogLayoutTests
{
    [Fact]
    public void Masked_and_revealed_password_inputs_share_the_full_field_width_and_value()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dialog = new FormDialog("Test", null).Note("Help text").Text("key", "Key", "test-value", password: true);
            try
            {
                var panel = (StackPanel)((ScrollViewer)dialog.Content).Content;
                var row = panel.Children.OfType<Grid>().Single();
                var masked = row.Children.OfType<PasswordBox>().Single();
                var shown = row.Children.OfType<TextBox>().Single();
                var reveal = row.Children.OfType<CheckBox>().Single();
                row.Measure(new Size(440, 100));
                row.Arrange(new Rect(0, 0, 440, 100));
                Assert.True(masked.ActualWidth > 300);
                var width = masked.ActualWidth;
                reveal.IsChecked = true;
                row.Measure(new Size(440, 100));
                row.Arrange(new Rect(0, 0, 440, 100));
                Assert.Equal(width, shown.ActualWidth, 1);
                Assert.Equal("test-value", shown.Text);
                shown.Text = "edited-value";
                reveal.IsChecked = false;
                Assert.Equal("edited-value", masked.Password);
                Assert.Equal("edited-value", dialog.Raw("key"));
                Assert.NotEqual(DependencyProperty.UnsetValue, panel.Children.OfType<TextBlock>().First().ReadLocalValue(TextBlock.ForegroundProperty));
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { dialog.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
