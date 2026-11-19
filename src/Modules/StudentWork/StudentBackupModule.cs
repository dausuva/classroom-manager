// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.StudentWork;

// Sequence is an invocation-local handle, never a source/destination path or file identifier.
public sealed record BackupPage(long Sequence, int Items, bool SupportsPaging, bool Complete, bool HasMore, bool AdvancesCursor);
public sealed record BackupCopyResult(int Copied, int UpToDate, int Failed);
public sealed record BackupSummary(int Copied, int UpToDate, int Pages);
public enum BackupFailure { UnsupportedPaging, IncompleteScan, CopyFailed, NoProgress }
public sealed class BackupRejectedException(BackupFailure failure) : Exception
{
    public BackupFailure Failure { get; } = failure;
}

/// <summary>One core-owned invocation. The module sees counts/status only, not file names or bytes.</summary>
public interface IStudentBackupRun
{
    Task<BackupPage> ScanNextAsync(CancellationToken cancellation);
    Task<BackupCopyResult> CopyAsync(BackupPage page, CancellationToken cancellation);
    void Commit(DateTime startedUtc);
}

/// <summary>All-pages/all-copies orchestration. Core owns scan validation, transfer and durable checkpoints.</summary>
public sealed class StudentBackupModule(TimeProvider time)
{
    public const string Id = "dausuva.student-work.backup";
    public const int ContractVersion = 1;

    public async Task<BackupSummary> RunAsync(IStudentBackupRun run, CancellationToken cancellation = default)
    {
        var started = time.GetUtcNow().UtcDateTime;
        int copied = 0, upToDate = 0, pages = 0;
        long sequence = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var page = await run.ScanNextAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (page.Items < 0 || page.Sequence != checked(sequence + 1)) throw new InvalidOperationException();
            sequence = page.Sequence;
            if (!page.SupportsPaging) throw new BackupRejectedException(BackupFailure.UnsupportedPaging);
            if (!page.Complete) throw new BackupRejectedException(BackupFailure.IncompleteScan);
            var outcome = await run.CopyAsync(page, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (outcome.Copied < 0 || outcome.UpToDate < 0 || outcome.Failed < 0) throw new InvalidOperationException();
            if (outcome.Failed > 0) throw new BackupRejectedException(BackupFailure.CopyFailed);
            copied = checked(copied + outcome.Copied);
            upToDate = checked(upToDate + outcome.UpToDate);
            pages = checked(pages + 1);
            if (!page.HasMore) break;
            if (page.Items == 0 || !page.AdvancesCursor) throw new BackupRejectedException(BackupFailure.NoProgress);
        }
        cancellation.ThrowIfCancellationRequested();
        run.Commit(started);
        return new(copied, upToDate, pages);
    }
}
