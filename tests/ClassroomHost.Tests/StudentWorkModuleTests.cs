// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.StudentWork;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-222: backup commit sequencing without file paths, transport or Host models.</summary>
public sealed class StudentWorkModuleTests
{
    static BackupPage Page(long sequence = 1, int items = 1, bool more = false, bool complete = true, bool paging = true, bool progress = true) =>
        new(sequence, items, paging, complete, more, progress);
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
    sealed class Run : IStudentBackupRun
    {
        public Queue<BackupPage> Pages { get; } = new();
        public List<string> Events { get; } = [];
        public DateTime? Committed { get; private set; }
        public Func<CancellationToken, Task<BackupPage>>? Scan { get; set; }
        public Func<BackupPage, CancellationToken, Task<BackupCopyResult>> Copy { get; set; } =
            (page, _) => Task.FromResult(new BackupCopyResult(page.Items, 0, 0));
        public Exception? CommitError { get; set; }
        public Task<BackupPage> ScanNextAsync(CancellationToken cancellation)
        { Events.Add("scan"); return Scan is null ? Task.FromResult(Pages.Dequeue()) : Scan(cancellation); }
        public Task<BackupCopyResult> CopyAsync(BackupPage page, CancellationToken cancellation)
        { Events.Add("copy"); return Copy(page, cancellation); }
        public void Commit(DateTime startedUtc)
        {
            Events.Add("commit");
            if (CommitError is { } error) throw error;
            Committed = startedUtc;
        }
    }

    [Fact]
    public async Task Every_page_is_copied_before_one_commit_at_the_original_start_time()
    {
        var clock = new Clock();
        var start = clock.Utc.UtcDateTime;
        var run = new Run { Copy = (page, _) =>
        {
            clock.Utc += TimeSpan.FromHours(1);
            return Task.FromResult(page.Sequence == 1 ? new BackupCopyResult(2, 1, 0) : new BackupCopyResult(1, 2, 0));
        }};
        run.Pages.Enqueue(Page(items: 3, more: true));
        run.Pages.Enqueue(Page(2, 3));
        var result = await new StudentBackupModule(clock).RunAsync(run);
        Assert.Equal(new BackupSummary(3, 3, 2), result);
        Assert.Equal(start, run.Committed);
        Assert.Equal(["scan", "copy", "scan", "copy", "commit"], run.Events);
    }

    [Theory]
    [InlineData(false, true, BackupFailure.UnsupportedPaging)]
    [InlineData(true, false, BackupFailure.IncompleteScan)]
    public async Task Unsupported_or_incomplete_scans_never_copy_or_commit(bool paging, bool complete, BackupFailure expected)
    {
        var run = new Run();
        run.Pages.Enqueue(Page(paging: paging, complete: complete));
        var failure = await Assert.ThrowsAsync<BackupRejectedException>(() => new StudentBackupModule(new Clock()).RunAsync(run));
        Assert.Equal(expected, failure.Failure);
        Assert.Equal(["scan"], run.Events);
        Assert.Null(run.Committed);
    }

    [Fact]
    public async Task A_later_incomplete_page_preserves_already_copied_work_without_a_success_checkpoint()
    {
        var run = new Run();
        run.Pages.Enqueue(Page(more: true));
        run.Pages.Enqueue(Page(2, complete: false));
        await Assert.ThrowsAsync<BackupRejectedException>(() => new StudentBackupModule(new Clock()).RunAsync(run));
        Assert.Equal(["scan", "copy", "scan"], run.Events);
        Assert.Null(run.Committed);
    }

    [Fact]
    public async Task Copy_failure_is_not_a_successful_empty_page()
    {
        var run = new Run { Copy = (_, _) => Task.FromResult(new BackupCopyResult(0, 0, 1)) };
        run.Pages.Enqueue(Page());
        var failure = await Assert.ThrowsAsync<BackupRejectedException>(() => new StudentBackupModule(new Clock()).RunAsync(run));
        Assert.Equal(BackupFailure.CopyFailed, failure.Failure);
        Assert.Null(run.Committed);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task A_continuation_must_have_items_and_advance_the_core_cursor(int items, bool progress)
    {
        var run = new Run();
        run.Pages.Enqueue(Page(items: items, more: true, progress: progress));
        var failure = await Assert.ThrowsAsync<BackupRejectedException>(() => new StudentBackupModule(new Clock()).RunAsync(run));
        Assert.Equal(BackupFailure.NoProgress, failure.Failure);
        Assert.Equal(["scan", "copy"], run.Events);
        Assert.Null(run.Committed);
    }

    [Fact]
    public async Task A_complete_empty_final_scan_is_a_valid_backup()
    {
        var run = new Run();
        run.Pages.Enqueue(Page(items: 0));
        Assert.Equal(new BackupSummary(0, 0, 1), await new StudentBackupModule(new Clock()).RunAsync(run));
        Assert.NotNull(run.Committed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_even_if_an_adapter_ignores_it_cannot_start_a_copy_or_commit_late_success(bool duringScan)
    {
        using var stop = new CancellationTokenSource();
        var run = new Run
        {
            Scan = _ => { if (duringScan) stop.Cancel(); return Task.FromResult(Page()); },
            Copy = (_, _) => { stop.Cancel(); return Task.FromResult(new BackupCopyResult(1, 0, 0)); },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StudentBackupModule(new Clock()).RunAsync(run, stop.Token));
        Assert.Equal(duringScan ? new[] { "scan" } : new[] { "scan", "copy" }, run.Events);
        Assert.Null(run.Committed);
    }

    [Fact]
    public async Task Nonconsecutive_page_handles_are_refused_before_copying_a_stale_page()
    {
        var run = new Run();
        run.Pages.Enqueue(Page(more: true));
        run.Pages.Enqueue(Page(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StudentBackupModule(new Clock()).RunAsync(run));
        Assert.Equal(["scan", "copy", "scan"], run.Events);
        Assert.Null(run.Committed);
    }

    [Fact]
    public async Task Invalid_copy_accounting_cannot_commit()
    {
        var run = new Run { Copy = (_, _) => Task.FromResult(new BackupCopyResult(-1, 0, 0)) };
        run.Pages.Enqueue(Page());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StudentBackupModule(new Clock()).RunAsync(run));
        Assert.Null(run.Committed);
    }

    [Fact]
    public async Task Counter_overflow_cannot_commit_a_wrapped_success_total()
    {
        var run = new Run { Copy = (_, _) => Task.FromResult(new BackupCopyResult(int.MaxValue, 0, 0)) };
        run.Pages.Enqueue(Page(more: true));
        run.Pages.Enqueue(Page(2));
        await Assert.ThrowsAsync<OverflowException>(() => new StudentBackupModule(new Clock()).RunAsync(run));
        Assert.Null(run.Committed);
    }

    [Fact]
    public async Task Commit_failure_is_propagated_without_a_success_summary()
    {
        var failure = new IOException("injected storage failure");
        var run = new Run { CommitError = failure };
        run.Pages.Enqueue(Page());
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => new StudentBackupModule(new Clock()).RunAsync(run)));
        Assert.Null(run.Committed);
    }

    [Fact]
    public void Assembly_references_only_frameworks_and_page_contract_contains_no_file_payloads()
    {
        FrameworkReferenceAssertions.AssertOnly(typeof(StudentBackupModule).Assembly);
        Assert.All(typeof(BackupPage).GetProperties(), property => Assert.Contains(property.PropertyType, new[] { typeof(long), typeof(int), typeof(bool) }));
    }
}
