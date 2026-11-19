// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class OperationActivityTests
{
    [Fact]
    public void Successful_operation_reports_determinate_progress_and_completion()
    {
        var operation = new OperationActivity("Send files", "2 selected computers", 2);

        operation.Start();
        operation.RecordSuccess("PC-01");
        operation.RecordSuccess("PC-02");
        operation.Finish();

        Assert.Equal("Completed", operation.State);
        Assert.Equal("No recovery action is needed.", operation.Recovery);
        Assert.Equal("2/2", operation.ProgressText);
        Assert.Equal(2, operation.Succeeded);
        Assert.Equal(0, operation.Failed);
        Assert.Equal(2, operation.Results.Count);
        Assert.True(operation.IsFinished);
    }

    [Fact]
    public void Mixed_result_remains_visible_as_completed_with_warnings()
    {
        var operation = new OperationActivity("Install", "2 selected computers", 2);

        operation.Start();
        operation.RecordSuccess("PC-01");
        operation.RecordFailure("PC-02", "Timed out");
        operation.Finish();

        Assert.Equal("Completed with warnings", operation.State);
        Assert.Equal("1 succeeded; 1 failed.", operation.Latest);
        Assert.Contains("Successful computers need no further action", operation.Recovery);
        var report = operation.FormatDetails();
        Assert.Contains("Succeeded  PC-01", report);
        Assert.Contains("Failed  PC-02  Timed out", report);
        Assert.Contains("Next step:", report);
    }

    [Fact]
    public void All_failures_mark_operation_failed()
    {
        var operation = new OperationActivity("Update", "1 selected computer", 1);

        operation.Start();
        operation.RecordFailure("PC-01", "Offline");
        operation.Finish();

        Assert.Equal("Failed", operation.State);
        Assert.Contains("review each error", operation.Recovery);
        Assert.True(operation.IsFinished);
    }

    [Theory]
    [InlineData(3, 0, "Completed")]
    [InlineData(0, 3, "Failed")]
    [InlineData(2, 1, "Completed with warnings")]
    public void Result_language_preserves_success_and_failure_distinctions(int succeeded, int failed, string state)
    {
        Assert.Equal(state, OperationResultLanguage.Format(succeeded, failed).State);
    }

    [Theory]
    [InlineData(0, "", "")]
    [InlineData(1, "1", "1 running")]
    [InlineData(3, "3", "3 running")]
    [InlineData(140, "99+", "140 running")]
    public void Operations_view_badge_counts_running_work(int running, string badge, string status)
    {
        Assert.Equal(badge, OperationViewLabel.Badge(running));
        Assert.Equal(status, OperationViewLabel.Status(running));
        Assert.Equal("_Operations", OperationViewLabel.Caption);
    }

    [Fact]
    public void Cancellation_preserves_completed_work_and_marks_unstarted_targets_skipped()
    {
        var operation = new OperationActivity("Action", "2 computers", 2);
        using var cancellation = new CancellationTokenSource();
        operation.Start();
        operation.EnableCancellation(cancellation);
        operation.RecordSuccess("PC-01");
        operation.Cancel();
        operation.RecordCancelled("PC-02");
        operation.FinishCancelled();

        Assert.Equal("Cancelled", operation.State);
        Assert.Equal("2/2", operation.ProgressText);
        Assert.Equal(1, operation.Cancelled);
        Assert.False(operation.CanCancel);
    }

    [Fact]
    public async Task Retry_failed_can_only_be_started_once()
    {
        var operation = new OperationActivity("Action", "1 computer", 1);
        var retries = 0;
        operation.Start();
        operation.RecordFailure("PC-01", "Offline");
        operation.Finish();
        operation.EnableRetry(() => { retries++; return Task.CompletedTask; });

        await operation.RetryFailedAsync();
        await operation.RetryFailedAsync();

        Assert.Equal(1, retries);
        Assert.False(operation.CanRetry);
    }

    [Fact]
    public void Preflight_warnings_remain_available_in_copied_details()
    {
        var operation = new OperationActivity("Install", "2 computers", 2,
            warnings: "The installer may wait for hidden interaction.");
        operation.Start();
        operation.RecordSuccess("PC-01");
        operation.RecordSuccess("PC-02");
        operation.Finish();

        Assert.True(operation.HasWarnings);
        Assert.Contains("Warnings:", operation.FormatDetails());
        Assert.Contains("hidden interaction", operation.FormatDetails());
    }
}
