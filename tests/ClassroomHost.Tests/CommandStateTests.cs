// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class CommandStateTests
{
    [Fact]
    public void Selection_rules_ignore_labels_and_restore_the_original_help_without_repeated_reasons()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var command = new Button { Content = "A renamed button", ToolTip = "Original description" };
                CommandState.SetId(command, "classroom.lock");
                CommandState.Update(command, 0);
                Assert.False(command.IsEnabled);
                Assert.Equal("Select at least one computer.", CommandState.GetDisabledReason(command));
                var disabledHelp = command.ToolTip;
                CommandState.Update(command, 0);
                Assert.Equal(disabledHelp, command.ToolTip);
                command.Content = "Pervadinta komanda";
                CommandState.Update(command, 1);
                Assert.True(command.IsEnabled);
                Assert.Equal("", CommandState.GetDisabledReason(command));
                Assert.Equal("Original description", command.ToolTip);
                var scan = new Button { IsEnabled = false };
                CommandState.SetId(scan, "computers.scan");
                CommandState.Update(scan, 1);
                Assert.False(scan.IsEnabled);
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
