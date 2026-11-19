// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-215: backup failure, cancellation and concurrent checkpoint behaviour, without real Host settings.</summary>
public sealed class BackupWorkflowTests
{
    static readonly RecentQuery Query = new() { AllFiles = true, SinceUtc = StudentBackup.Never };

    [Theory]
    [InlineData("old-client")]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("scan-limit")]
    [InlineData("disk-full")]
    [InlineData("disconnect")]
    public async Task An_incomplete_backup_never_commits(string failure)
    {
        var committed = false;
        var page = new RecentScan([], failure == "scan-limit", failure == "missing" ? ["Documents"] : [])
        { SupportsPaging = failure != "old-client", Errors = failure == "unreadable" ? ["Access denied"] : [] };
        await Assert.ThrowsAnyAsync<IOException>(() => StudentBackup.RunWorkflowAsync(Query,
            (_, _) => Task.FromResult(page), (_, _) => failure == "disconnect" ? throw new EndOfStreamException() :
                Task.FromResult(new RecentCopyOutcome(0, 0, failure == "disk-full" ? ["Disk full"] : [], [])),
            _ => committed = true, TimeProvider.System));
        Assert.False(committed);
    }

    [Fact]
    public async Task Cancellation_after_copy_does_not_commit_and_a_retry_can_finish()
    {
        using var stop = new CancellationTokenSource();
        var committed = false;
        var page = new RecentScan([], false, []) { SupportsPaging = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StudentBackup.RunWorkflowAsync(Query,
            (_, _) => Task.FromResult(page), (_, _) =>
            {
                stop.Cancel();
                return Task.FromResult(new RecentCopyOutcome(0, 0, [], []));
            }, _ => committed = true, TimeProvider.System, stop.Token));
        Assert.False(committed);
        await StudentBackup.RunWorkflowAsync(Query, (_, _) => Task.FromResult(page),
            (_, _) => Task.FromResult(new RecentCopyOutcome(0, 0, [], [])), _ => committed = true, TimeProvider.System);
        Assert.True(committed);
    }

    [Fact]
    public void Concurrent_checkpoints_preserve_all_computers_and_never_move_backwards()
    {
        var root = Path.Combine(Path.GetTempPath(), "cm-checkpoints-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "backups.json");
        var now = DateTime.UtcNow;
        try
        {
            Parallel.For(0, 40, index => StudentBackup.RecordBackup("pc" + index, now, path));
            Parallel.For(0, 40, index => StudentBackup.RecordBackup("PC" + index, now.AddDays(-1), path));
            for (int index = 0; index < 40; index++) Assert.Equal(now, StudentBackup.LastBackupUtc("pc" + index, path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp*"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static RecentEntry Entry(string path, DateTime modified) => new("root", path, path, false, 1, modified);

    [Fact]
    public async Task Core_cursor_and_query_are_detached_from_provider_mutation_while_copying()
    {
        var modified = new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
        var folders = new List<string> { "selected-folder" };
        var entries = new List<RecentEntry> { Entry("a", modified), Entry("b", modified) };
        var queries = new List<RecentQuery>();
        var copiedPages = 0;
        DateTime? committed = null;
        var result = await StudentBackup.RunWorkflowAsync(Query with { Folders = folders }, (query, _) =>
        {
            queries.Add(query);
            return Task.FromResult(queries.Count == 1
                ? new RecentScan(entries, true, []) { SupportsPaging = true, HasMore = true }
                : new RecentScan([Entry("c", modified)], false, []) { SupportsPaging = true });
        }, (items, _) =>
        {
            copiedPages++;
            var count = items.Count;
            if (copiedPages == 1)
            {
                entries.Clear();
                folders.Clear();
                Assert.Equal(2, items.Count);
            }
            return Task.FromResult(new RecentCopyOutcome(count, 0, [], []));
        }, started => committed = started, TimeProvider.System);
        Assert.Equal("3 file(s) copied, 0 already there", result);
        Assert.Equal(2, copiedPages);
        Assert.All(queries, query => Assert.Equal(["selected-folder"], query.Folders));
        Assert.Equal(modified, queries[1].AfterModifiedUtc);
        Assert.Equal("b", queries[1].AfterPath);
        Assert.NotNull(committed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Empty_or_nonadvancing_continuations_never_advance_the_checkpoint(bool empty)
    {
        var at = DateTime.UtcNow;
        var committed = false;
        var page = new RecentScan(empty ? [] : [Entry("SAME", at)], true, []) { SupportsPaging = true, HasMore = true };
        var error = await Assert.ThrowsAsync<IOException>(() => StudentBackup.RunWorkflowAsync(
            Query with { AfterModifiedUtc = at, AfterPath = "same" }, (_, _) => Task.FromResult(page),
            (_, _) => Task.FromResult(new RecentCopyOutcome(0, 0, [], [])), _ => committed = true, TimeProvider.System));
        Assert.Equal("Backup scan returned no pagination progress.", error.Message);
        Assert.False(committed);
    }

    [Fact]
    public async Task A_later_unreadable_page_does_not_commit_an_earlier_successful_copy()
    {
        var scans = 0;
        var copies = 0;
        var committed = false;
        var error = await Assert.ThrowsAsync<IOException>(() => StudentBackup.RunWorkflowAsync(Query, (_, _) =>
            Task.FromResult(++scans == 1
                ? new RecentScan([Entry("a", DateTime.UtcNow)], true, []) { SupportsPaging = true, HasMore = true }
                : new RecentScan([], false, []) { SupportsPaging = true, Errors = ["access denied"] }),
            (_, _) => { copies++; return Task.FromResult(new RecentCopyOutcome(1, 0, [], [])); },
            _ => committed = true, TimeProvider.System));
        Assert.Equal("Backup incomplete: access denied", error.Message);
        Assert.Equal(1, copies);
        Assert.False(committed);
    }

    [Fact]
    public async Task Cancellation_after_an_ignoring_scan_prevents_copy_and_commit()
    {
        using var stop = new CancellationTokenSource();
        var copied = false;
        var committed = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StudentBackup.RunWorkflowAsync(Query, (_, _) =>
        {
            stop.Cancel();
            return Task.FromResult(new RecentScan([], false, []) { SupportsPaging = true });
        }, (_, _) => { copied = true; return Task.FromResult(new RecentCopyOutcome(0, 0, [], [])); },
            _ => committed = true, TimeProvider.System, stop.Token));
        Assert.False(copied);
        Assert.False(committed);
    }

    [Fact]
    public async Task Synchronous_adapter_failures_keep_the_existing_async_fault_contract_and_exception_identity()
    {
        var failure = new EndOfStreamException("injected disconnect");
        Task<string>? pending = null;
        var synchronous = Record.Exception(() =>
        {
            pending = StudentBackup.RunWorkflowAsync(Query, (_, _) => throw failure,
                (_, _) => throw new Exception("Must not copy"), _ => throw new Exception("Must not commit"), TimeProvider.System);
        });
        Assert.Null(synchronous);
        Assert.NotNull(pending);
        Assert.Same(failure, await Assert.ThrowsAsync<EndOfStreamException>(() => pending));
    }
}
