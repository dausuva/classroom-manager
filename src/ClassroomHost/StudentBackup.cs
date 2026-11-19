// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;
using ClassroomManager.Shared;
using ClassroomManager.StudentWork;

namespace ClassroomManager.Host;

/// <summary>
/// "Back up student work" (T-214): the files changed in a computer's watched folders (Recent files: Desktop and Documents unless set
/// otherwise) since its last successful backup, copied to Backups\&lt;computer&gt; under the Recent files folder on this PC. The first
/// backup of a computer takes everything in those folders. Backups are not deleted after the Recent files retention period.
/// </summary>
public static class StudentBackup
{
    public static string RecordPath => Path.Combine(HostSettings.DataFolder, "student-backups.json");

    /// <summary>The start of "everything" for a computer that was never backed up.</summary>
    public static readonly DateTime Never = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Where backups go: Backups in the Recent files folder.</summary>
    public static string Destination(RecentFilesSettings settings) =>
        Path.Combine(settings.Destination.Length > 0 ? settings.Destination : RecentFilesPlan.DefaultDestination(), "Backups");

    /// <summary>When the computer's last backup that worked started, or <see cref="Never"/>.</summary>
    public static DateTime LastBackupUtc(string computerKey, string? path = null) =>
        Read(path ?? RecordPath).TryGetValue(computerKey, out var last) ? DateTime.SpecifyKind(last, DateTimeKind.Utc) : Never;

    /// <summary>Records a backup that worked, from the moment it started (so a file saved during the copy is taken next time).</summary>
    public static void RecordBackup(string computerKey, DateTime startedUtc, string? path = null)
    {
        path ??= RecordPath;
        JsonRecordFile.Use(path, () => new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase), records =>
        {
            var existing = records.Keys.FirstOrDefault(key => key.Equals(computerKey, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && records[existing] > startedUtc) return false;
            records[existing ?? computerKey] = startedUtc;
            return true;
        }, write: true);
    }

    static Dictionary<string, DateTime> Read(string path)
    {
        try { return JsonRecordFile.Use(path, () => new Dictionary<string, DateTime>(),
            records => new Dictionary<string, DateTime>(records, StringComparer.OrdinalIgnoreCase), write: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>What a backup of one computer asks for: every file in its watched folders changed since its last backup.</summary>
    public static RecentQuery Query(RecentFilesSettings settings, ClientInfo client, DateTime sinceUtc) => new()
    {
        Folders = RecentFilesPlan.WatchedFolders(settings, client),
        SinceUtc = sinceUtc,
        AllFiles = true,
        MaxItems = RecentFiles.MaxItemsLimit,
    };

    /// <summary>
    /// Backs up one computer and returns what it did. It counts as done (and the next backup starts from now) only when every file was
    /// copied and the list was not cut short; otherwise it throws, so the schedule tries again later. The module reaches the computer and
    /// the destination folder only through the ports of <see cref="ModuleRuns"/>.
    /// </summary>
    public static async Task<string> RunAsync(ClientSession session, ClientInfo client, HostSettings settings, CancellationToken ct = default)
    {
        if (!BuiltInModules.Enabled(settings, BuiltInModules.StudentWork)) throw new InvalidOperationException(BuiltInModules.Disabled);
        var query = Query(settings.RecentFiles, client, LastBackupUtc(RecentFilesPlan.ComputerKey(client)));
        var destination = Destination(settings.RecentFiles);
        var run = ModuleRuns.Open(BuiltInModules.StudentWork, [(session, client)], sink => sink == PortBackupSource.Sink ? destination : null);
        var labels = query.Folders.Distinct(StringComparer.Ordinal).ToDictionary(folder => folder, RecentFilesPlan.FolderLabel, StringComparer.Ordinal);
        var summary = await StudentBackupRunner.RunAsync(ToBackupQuery(query), new PortBackupSource(run, run.Computers[0], labels),
            started => RecordBackup(RecentFilesPlan.ComputerKey(client), started), TimeProvider.System, ct);
        return StudentBackupPresentation.Summary(summary.Copied, summary.UpToDate);
    }

    /// <summary>Transport-independent backup orchestration; commit only after every page and copy succeeds.</summary>
    public static async Task<string> RunWorkflowAsync(RecentQuery query,
        Func<RecentQuery, CancellationToken, Task<RecentScan>> scanPage,
        Func<IReadOnlyList<RecentEntry>, CancellationToken, Task<RecentCopyOutcome>> copyPage,
        Action<DateTime> commit, TimeProvider time, CancellationToken ct = default)
    {
        var summary = await StudentBackupRunner.RunAsync(ToBackupQuery(query), new DelegateSource(query, scanPage, copyPage), commit, time, ct);
        return StudentBackupPresentation.Summary(summary.Copied, summary.UpToDate);
    }

    static BackupQuery ToBackupQuery(RecentQuery query) => new(query.Folders, query.SinceUtc, query.MaxItems, query.AfterModifiedUtc, query.AfterPath);

    /// <summary>Adapts the Host's own scan and copy functions to the module's source: for tests and for any caller that has no ports.</summary>
    sealed class DelegateSource(RecentQuery original,
        Func<RecentQuery, CancellationToken, Task<RecentScan>> scanPage,
        Func<IReadOnlyList<RecentEntry>, CancellationToken, Task<RecentCopyOutcome>> copyPage) : IBackupSource
    {
        public async Task<BackupScan> ScanAsync(BackupQuery query, CancellationToken cancellation)
        {
            var scan = await scanPage(original with
            {
                Folders = Array.AsReadOnly(query.Folders.ToArray()), Extensions = Array.AsReadOnly(original.Extensions.ToArray()),
                AfterModifiedUtc = query.AfterModifiedUtc, AfterPath = query.AfterPath,
            }, cancellation);
            return new(scan.Items.Select(item => new BackupEntry(item.Root, item.Path, item.RelativePath, item.IsFolder, item.Size, item.ModifiedUtc)).ToList(),
                scan.Truncated, scan.Missing, scan.SupportsPaging, scan.HasMore, scan.Errors);
        }

        public async Task<BackupCopyOutcome> CopyAsync(IReadOnlyList<BackupEntry> entries, CancellationToken cancellation)
        {
            var outcome = await copyPage(entries.Select(entry => new RecentEntry(entry.Root, entry.Path, entry.RelativePath, entry.IsFolder, entry.Size, entry.ModifiedUtc)).ToList(), cancellation);
            return new(outcome.Copied, outcome.UpToDate, outcome.Failed);
        }
    }
}
