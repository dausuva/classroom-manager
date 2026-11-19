// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public class OperationUndoTests
{
    [Fact]
    public async Task A_finished_operation_can_be_taken_back_once_on_the_computers_it_succeeded_on()
    {
        var operation = new OperationActivity("Lock screen", "3 computers", 3);
        int undone = 0;
        operation.Start();
        operation.RecordSuccess("PC-01");
        operation.RecordSuccess("PC-02");
        operation.RecordFailure("PC-03", "Connection timed out.");

        // Nothing to take back while it is still running, or before anyone said how.
        operation.EnableUndo("Unlock screen", () => { undone++; return Task.CompletedTask; });
        Assert.False(operation.CanUndo);
        operation.Finish();

        Assert.True(operation.CanUndo);
        Assert.Equal("Unlock screen", operation.UndoLabel);

        await operation.UndoAsync();
        await operation.UndoAsync();
        Assert.Equal(1, undone);
        Assert.False(operation.CanUndo);
    }

    [Fact]
    public void An_operation_that_succeeded_nowhere_or_has_no_counterpart_offers_no_undo()
    {
        var failed = new OperationActivity("Lock screen", "1 computer", 1);
        failed.Start();
        failed.RecordFailure("PC-01", "Offline");
        failed.EnableUndo("Unlock screen", () => Task.CompletedTask);
        failed.Finish();
        Assert.False(failed.CanUndo);

        var plain = new OperationActivity("Collect files", "1 computer", 1);
        plain.Start();
        plain.RecordSuccess("PC-01");
        plain.Finish();
        Assert.False(plain.CanUndo);
        Assert.Equal("", plain.UndoLabel);
    }
}
