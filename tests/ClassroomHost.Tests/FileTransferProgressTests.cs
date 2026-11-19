// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class FileTransferProgressTests
{
    sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => throw new Exception("Use monotonic time");
    }

    [Theory]
    [InlineData(FileTransferDirection.Upload, "Uploading")]
    [InlineData(FileTransferDirection.Download, "Downloading")]
    public void Status_shows_payload_megabits_and_percent_not_megabytes(FileTransferDirection direction, string action)
    {
        var progress = new FileTransferProgress(direction, 1_000_000, 2_000_000, TimeSpan.FromSeconds(2));
        Assert.Equal(50, progress.Percent);
        Assert.Equal(4, progress.MegabitsPerSecond);
        Assert.Equal(action + ": 50% · 4.0 Mb/s · PC", FileTransferProgressText.Format(progress, "PC"));
        var operation = new OperationActivity("Copy", "PC", 1);
        operation.Start();
        operation.ReportTransfer("PC", progress);
        Assert.Contains("50%", operation.Latest);
        Assert.Contains("4.0 Mb/s", operation.Latest);
        operation.RecordSuccess("PC");
        operation.Finish();
        var summary = operation.Latest;
        operation.ReportTransfer("PC", progress);
        Assert.Equal(summary, operation.Latest);
    }

    [Fact]
    public void Notifications_are_bounded_but_initial_final_and_empty_file_completion_are_not_lost()
    {
        var clock = new Clock();
        var updates = new List<FileTransferProgress>();
        var meter = new FileTransferMeter(FileTransferDirection.Upload, 100, updates.Add, clock);
        meter.Report(0);
        clock.Ticks = 100;
        meter.Report(10);
        Assert.Single(updates);
        clock.Ticks = 250;
        meter.Report(50);
        clock.Ticks = 251;
        meter.Report(100, complete: true);
        Assert.Equal([0, 50, 100], updates.Select(update => update.Percent));
        updates.Clear();
        var empty = new FileTransferMeter(FileTransferDirection.Upload, 0, updates.Add, clock);
        empty.Report(0);
        empty.Report(0, complete: true);
        Assert.Equal([0, 100], updates.Select(update => update.Percent));
        Assert.All(updates, update => Assert.Equal(0, update.MegabitsPerSecond));
    }

    [Fact]
    public void Download_rate_includes_the_first_request_and_cancellation_summary_cannot_be_overwritten()
    {
        var clock = new Clock { Ticks = 2000 };
        var updates = new List<FileTransferProgress>();
        var meter = new FileTransferMeter(FileTransferDirection.Download, 1_000_000, updates.Add, clock, startedTimestamp: 0);
        meter.Report(1_000_000, complete: true);
        Assert.Equal(4, Assert.Single(updates).MegabitsPerSecond);
        var operation = new OperationActivity("Copy", "PC", 1);
        using var cancellation = new CancellationTokenSource();
        operation.Start();
        operation.EnableCancellation(cancellation);
        operation.Cancel();
        var summary = operation.Latest;
        operation.ReportTransfer("PC", updates[0]);
        Assert.Equal(summary, operation.Latest);
    }
}
