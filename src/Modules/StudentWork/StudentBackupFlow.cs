// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.StudentWork;

/// <summary>A file (or folder) found on a student computer. <see cref="Path"/> is its path there; the module never reads it, it only names it.</summary>
public sealed record BackupEntry(string Root, string Path, string RelativePath, bool IsFolder, long Size, DateTime ModifiedUtc);

/// <summary>What one scan asks for: the watched folders, everything changed since a time, at most <see cref="MaxItems"/> files, after a cursor.</summary>
public sealed record BackupQuery(IReadOnlyList<string> Folders, DateTime SinceUtc, int MaxItems, DateTime? AfterModifiedUtc = null, string AfterPath = "");

/// <summary>One page of a scan, newest first.</summary>
public sealed record BackupScan(IReadOnlyList<BackupEntry> Items, bool Truncated, IReadOnlyList<string> Missing, bool SupportsPaging, bool HasMore, IReadOnlyList<string> Errors);

/// <summary>What copying one page did; <see cref="Failed"/> names each file that could not be copied.</summary>
public sealed record BackupCopyOutcome(int Copied, int UpToDate, IReadOnlyList<string> Failed);

/// <summary>Where a backup gets its pages and puts its copies. The Host's ports are one source; a test can be another.</summary>
public interface IBackupSource
{
    Task<BackupScan> ScanAsync(BackupQuery query, CancellationToken cancellation);

    Task<BackupCopyOutcome> CopyAsync(IReadOnlyList<BackupEntry> entries, CancellationToken cancellation);
}

/// <summary>Runs one backup: pages and copies through an <see cref="IBackupSource"/>, and records the checkpoint only when every page was copied.</summary>
public static class StudentBackupRunner
{
    /// <exception cref="IOException">The backup could not finish; the message says why. Nothing was recorded.</exception>
    public static async Task<BackupSummary> RunAsync(BackupQuery query, IBackupSource source, Action<DateTime> commit, TimeProvider time, CancellationToken cancellation = default)
    {
        var run = new BackupSession(query, source, commit);
        try { return await new StudentBackupModule(time).RunAsync(run, cancellation); }
        catch (BackupRejectedException error) { throw run.Describe(error.Failure); }
    }
}

/// <summary>Cursor, page and copy bookkeeping for one backup. It hands the module counts and status, never file names or bytes.</summary>
sealed class BackupSession : IStudentBackupRun
{
    BackupQuery _query;
    readonly IBackupSource _source;
    readonly Action<DateTime> _commit;
    BackupScan? _scan;
    BackupPage? _page;
    IReadOnlyList<string> _copyFailures = [];
    bool _copied, _committed;

    public BackupSession(BackupQuery query, IBackupSource source, Action<DateTime> commit)
    {
        _query = query with { Folders = Array.AsReadOnly(query.Folders.ToArray()) };
        _source = source;
        _commit = commit;
    }

    public async Task<BackupPage> ScanNextAsync(CancellationToken cancellation)
    {
        if (_committed) throw new InvalidOperationException();
        if (_page is not null)
        {
            if (!_copied || !_page.HasMore || !_page.AdvancesCursor || _scan!.Items.Count == 0) throw new InvalidOperationException();
            var last = _scan.Items[^1];
            _query = _query with { AfterModifiedUtc = last.ModifiedUtc, AfterPath = last.Path };
        }
        var scan = await _source.ScanAsync(_query, cancellation);
        _scan = scan with { Items = Array.AsReadOnly(scan.Items.ToArray()), Missing = Array.AsReadOnly(scan.Missing.ToArray()),
            Errors = Array.AsReadOnly(scan.Errors.ToArray()) };
        var advances = _scan.Items.Count > 0 && (_query.AfterModifiedUtc is not { } after ||
            _scan.Items[^1].ModifiedUtc < after || _scan.Items[^1].ModifiedUtc == after &&
            StringComparer.OrdinalIgnoreCase.Compare(_scan.Items[^1].Path, _query.AfterPath) > 0);
        _copied = false;
        _copyFailures = [];
        _page = new(checked((_page?.Sequence ?? 0) + 1), _scan.Items.Count, _scan.SupportsPaging,
            _scan.Missing.Count == 0 && _scan.Errors.Count == 0 && !(_scan.Truncated && !_scan.HasMore), _scan.HasMore, advances);
        return _page;
    }

    public async Task<BackupCopyResult> CopyAsync(BackupPage page, CancellationToken cancellation)
    {
        if (_committed || _copied || _page != page || _scan is null || !page.SupportsPaging || !page.Complete) throw new InvalidOperationException();
        var outcome = await _source.CopyAsync(_scan.Items, cancellation);
        _copyFailures = Array.AsReadOnly(outcome.Failed.ToArray());
        _copied = _copyFailures.Count == 0 && outcome.Copied >= 0 && outcome.UpToDate >= 0;
        return new(outcome.Copied, outcome.UpToDate, _copyFailures.Count);
    }

    public void Commit(DateTime startedUtc)
    {
        if (_committed || !_copied || _page is null || _page.HasMore || !_page.SupportsPaging || !_page.Complete) throw new InvalidOperationException();
        _commit(startedUtc);
        _committed = true;
    }

    public IOException Describe(BackupFailure failure) => failure switch
    {
        BackupFailure.UnsupportedPaging => new IOException("Update the Client before backing up: this build cannot report complete paged scans."),
        BackupFailure.IncompleteScan => new IOException("Backup incomplete: " +
            string.Join("; ", _scan!.Missing.Concat(_scan.Errors).DefaultIfEmpty("scan limit reached"))),
        BackupFailure.CopyFailed => new IOException("Backup incomplete: " + _copyFailures[0]),
        BackupFailure.NoProgress => new IOException("Backup scan returned no pagination progress."),
        _ => throw new InvalidOperationException(),
    };
}
