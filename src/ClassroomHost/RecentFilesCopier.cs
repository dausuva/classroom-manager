// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>What one copy run did on one computer.</summary>
public sealed record RecentCopyOutcome(int Copied, int UpToDate, IReadOnlyList<string> Failed, IReadOnlyList<(string LocalPath, DateTime ModifiedUtc)> Written);

/// <summary>Recent files (T-212) over a connection: asks a computer what changed and copies it to this PC.</summary>
public static class RecentFilesCopier
{
    /// <summary>The start of "everything" when a whole folder is copied.</summary>
    static readonly DateTime Always = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>What changed in the watched folders, as the signed-in student sees them.</summary>
    public static async Task<RecentScan> ListAsync(ClientSession session, RecentQuery query, CancellationToken ct = default) =>
        RecentScan.FromJson(await session.CallAtAsync(Levels.User, Cmd.FsRecent, query.ToArgs(), ct));

    /// <summary>
    /// Copies the entries into this computer's folder under <paramref name="destination"/>; a folder brings everything in it. A file is
    /// downloaded under a temporary name and only then replaces the earlier copy, so a failed download leaves the earlier copy as it was.
    /// One file that cannot be read there or written here is named in the result and the rest go on; a lost connection ends the run.
    /// </summary>
    public static async Task<RecentCopyOutcome> CopyAsync(ClientSession session, ClientInfo client, IEnumerable<RecentEntry> entries,
        string destination, bool overwrite, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var failed = new List<string>();
        var files = new List<(RecentEntry Entry, string? Local)>();
        foreach (var entry in entries)
        {
            if (!entry.IsFolder) { files.Add((entry, RecentFilesPlan.LocalPath(destination, client, entry))); continue; }
            var inside = await ListAsync(session, new RecentQuery
            {
                Folders = [entry.Path], SinceUtc = Always, AllFiles = true, MaxItems = RecentFiles.MaxItemsLimit,
            }, ct);
            if (inside.Missing.Count > 0) failed.Add($"{entry.Path}: the folder no longer exists.");
            if (inside.Truncated) failed.Add($"{entry.Path}: only the newest {inside.Items.Count} files were copied.");
            foreach (var file in inside.Items)
            {
                var placed = file with { Root = entry.Root };
                files.Add((placed, RecentFilesPlan.LocalPath(destination, client, placed, entry.RelativePath)));
            }
        }

        int copied = 0, upToDate = 0, number = 0;
        var written = new List<(string, DateTime)>();
        var unique = files.GroupBy(file => file.Local ?? file.Entry.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        foreach (var (entry, local) in unique)
        {
            number++;
            if (local is null) { failed.Add($"{entry.Path}: its name would leave the destination folder, so it was not copied."); continue; }
            var existing = File.Exists(local) ? new FileInfo(local) : null;
            if (!RecentFilesPlan.NeedsCopy(entry, existing is null ? null : new LocalFile(existing.Length, existing.LastWriteTimeUtc), overwrite))
            {
                upToDate++;
                continue;
            }
            var partial = local + ".cm-partial";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                progress?.Report($"Copying {number} of {unique.Count}: {Path.GetFileName(entry.Path)}");
                await session.DownloadAsync(entry.Path, partial, ct: ct);
                File.SetLastWriteTimeUtc(partial, entry.ModifiedUtc);
                File.Move(partial, local, overwrite: true);
                written.Add((local, entry.ModifiedUtc));
                copied++;
            }
            // A lost connection (the computer went to sleep, say) ends the run: every later file would fail the same way. The session
            // reports it with a connection error code, which the per-file failures below never carry.
            catch (Exception ex) when (IsFileFailure(ex))
            {
                failed.Add($"{entry.Path}: {ex.Message}");
                TryDelete(partial);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
        }
        return new RecentCopyOutcome(copied, upToDate, failed, written);
    }

    /// <summary>
    /// True when an exception while copying one file is that file's own failure (the computer could not read it, this PC could not write it):
    /// the run goes on with the next one. A lost connection is not, and ends the run.
    /// </summary>
    public static bool IsFileFailure(Exception ex) =>
        ex is RemoteException or UnauthorizedAccessException ||
        ex is IOException and not EndOfStreamException && ex.InnerException is not System.Net.Sockets.SocketException &&
        !ErrorClassifier.IsUnreachable(ex.Message);

    /// <summary>
    /// Deletes the copies whose time is up (<see cref="RecentFilesSettings.KeepDays"/>), unless someone changed them here, and the folders
    /// that leaves empty under <paramref name="destination"/>. Returns how many files went. Records of files already gone are dropped.
    /// </summary>
    public static int Prune(RecentFilesSettings settings, string destination, DateTime nowUtc)
    {
        int deleted = 0;
        var root = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
        foreach (var record in RecentFilesPlan.Expired(settings.Copies, nowUtc, settings.KeepDays))
        {
            settings.Copies.Remove(record);
            var info = new FileInfo(record.LocalPath);
            // Only what was copied into the destination is ever deleted here.
            if (!info.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !info.Exists) continue;
            if (!RecentFilesPlan.MayDelete(record, info.LastWriteTimeUtc)) continue;
            try { info.Delete(); deleted++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            for (var dir = info.Directory; dir is not null && dir.FullName.TrimEnd('\\') + "\\" != root &&
                 dir.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase); dir = dir.Parent)
            {
                try { if (dir.EnumerateFileSystemInfos().Any()) break; dir.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
            }
        }
        return deleted;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
