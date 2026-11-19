// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Documents;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class FindParentTests
{
    /// <summary>The 1.2.0.307 Host closed when a computer's text was double-clicked: the click landed on a Run.</summary>
    [Fact]
    public void A_click_on_a_piece_of_text_finds_the_element_around_it()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var run = new Run("PC-01");
                var text = new TextBlock(run);
                var border = new Border { Child = text };
                Assert.Same(text, Ui.FindParent<TextBlock>(run));
                Assert.Same(border, Ui.FindParent<Border>(run));
                Assert.Null(Ui.FindParent<DataGrid>(run));
                Assert.Null(Ui.FindParent<Border>(null));
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
