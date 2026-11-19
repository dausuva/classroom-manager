// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>One editable file type group of Recent files, as the Host keeps it.</summary>
public sealed class FileTypeGroupSetting
{
    public string Name { get; set; } = "";
    public List<string> Extensions { get; set; } = new();

    public FileTypeGroup ToGroup() => new(Name, Extensions);
}

/// <summary>A file Recent files copied to this PC: where it is, when, and its change time then, for the retention clean-up.</summary>
public sealed class RecentCopyRecord
{
    public string LocalPath { get; set; } = "";
    public DateTime CopiedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }
}

/// <summary>What Recent files (T-212) remembers on the Host. Nothing of it is kept on the student computers.</summary>
public sealed class RecentFilesSettings
{
    /// <summary>The watched folders of each computer (by <see cref="RecentFilesPlan.ComputerKey"/>); a computer not listed watches the defaults.</summary>
    public Dictionary<string, List<string>> WatchedFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file type groups, editable under Manage types. Starts as <see cref="FileTypes.Defaults"/>.</summary>
    public List<FileTypeGroupSetting> TypeGroups { get; set; } = RecentFilesPlan.DefaultGroups();

    /// <summary>The kinds ticked last time: group names, <see cref="FileTypes.Folders"/> and <see cref="FileTypes.Other"/>.</summary>
    public List<string> CheckedKinds { get; set; } = [FileTypes.Documents, FileTypes.Images, FileTypes.SourceCode];

    /// <summary>The time window chosen last time (a <see cref="RecentFilesPlan.Windows"/> name).</summary>
    public string Window { get; set; } = RecentFilesPlan.DefaultWindow;

    /// <summary>The folder on this PC the copies go to; empty for Documents\Recent files of this user.</summary>
    public string Destination { get; set; } = "";

    /// <summary>Replace the earlier copy of a file when it changed (the default), instead of keeping the first copy.</summary>
    public bool Overwrite { get; set; } = true;

    /// <summary>How many days copies are kept; 0 keeps them until they are deleted by hand.</summary>
    public int KeepDays { get; set; } = RecentFilesPlan.DefaultKeepDays;

    /// <summary>The files copied so far, for <see cref="KeepDays"/>.</summary>
    public List<RecentCopyRecord> Copies { get; set; } = new();
}

/// <summary>A time window of Recent files: a name, and either minutes back from now or a rule for its start.</summary>
public sealed record RecentWindow(string Name, int? Minutes, Func<DateTime, DateTime>? StartLocal = null);

/// <summary>The decisions of Recent files that need no window and no connection, so they can be tested on their own.</summary>
public static class RecentFilesPlan
{
    public const int DefaultKeepDays = 30;
    public const string DefaultWindow = "Last 30 minutes";
    public const string CustomWindow = "Custom...";
    /// <summary>How many copy records are kept; the oldest go first.</summary>
    public const int MaxRecords = 20_000;

    public static IReadOnlyList<RecentWindow> Windows { get; } =
    [
        new("Last 10 minutes", 10),
        new(DefaultWindow, RecentFiles.DefaultMinutes),
        new("Last hour", 60),
        new("Last 2 hours", 120),
        new("Today", null, now => now.Date),
        new("Last 7 days", 7 * 24 * 60),
        new(CustomWindow, null),
    ];

    /// <summary>The kinds the window offers, in their order: the groups, then folders and other files.</summary>
    public static List<string> Kinds(IEnumerable<FileTypeGroupSetting> groups) =>
        groups.Select(group => group.Name).Append(FileTypes.Folders).Append(FileTypes.Other).ToList();

    public static List<FileTypeGroupSetting> DefaultGroups() =>
        FileTypes.Defaults.Select(group => new FileTypeGroupSetting { Name = group.Name, Extensions = group.Extensions.ToList() }).ToList();

    /// <summary>
    /// How a computer is known across sessions: its Client id, or its address before it reported one. Lower case, because the saved
    /// settings read back into a dictionary that compares exactly.
    /// </summary>
    public static string ComputerKey(ClientInfo client) => (client.ClientId.Length > 0 ? client.ClientId : client.Address).Trim().ToLowerInvariant();

    /// <summary>The folders watched on a computer: its own list, or Desktop and Documents.</summary>
    public static IReadOnlyList<string> WatchedFolders(RecentFilesSettings settings, ClientInfo client) =>
        settings.WatchedFolders.TryGetValue(ComputerKey(client), out var folders) && folders.Count > 0 ? folders : RecentFiles.DefaultFolders;

    /// <summary>Sets a computer's watched folders from what was typed (one per line); the defaults, or nothing typed, remove its own list.</summary>
    public static void SetWatchedFolders(RecentFilesSettings settings, ClientInfo client, string typed)
    {
        var folders = ParseFolders(typed);
        var key = ComputerKey(client);
        if (folders.Count == 0 || folders.SequenceEqual(RecentFiles.DefaultFolders, StringComparer.OrdinalIgnoreCase)) settings.WatchedFolders.Remove(key);
        else settings.WatchedFolders[key] = folders;
    }

    /// <summary>The folders of a typed list, one per line, each once; quotes from Explorer's "Copy as path" are removed.</summary>
    public static List<string> ParseFolders(string? typed) =>
        (typed ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => PathText.Clean(line).Trim()).Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(RecentFiles.MaxFolders).ToList();

    /// <summary>
    /// What to ask a computer for. The Client is sent the extensions of the ticked groups (or asked for every file when Other files is
    /// ticked) and whether to list folders; <see cref="Wanted"/> then keeps only the ticked kinds.
    /// </summary>
    public static RecentQuery Query(IReadOnlyList<string> folders, IEnumerable<FileTypeGroupSetting> groups, IReadOnlyCollection<string> kinds,
        DateTime sinceUtc, DateTime? untilUtc, int? withinMinutes) => new()
    {
        Folders = folders,
        SinceUtc = sinceUtc,
        UntilUtc = untilUtc,
        WithinMinutes = withinMinutes,
        AllFiles = kinds.Contains(FileTypes.Other),
        IncludeFolders = kinds.Contains(FileTypes.Folders),
        Extensions = groups.Where(group => kinds.Contains(group.Name))
            .SelectMany(group => group.Extensions).Select(FileTypes.NormalizeExtension).Where(ext => ext.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>The start and end of a window, and its minutes when it counts back from now (on the Client's clock).</summary>
    public static (DateTime SinceUtc, DateTime? UntilUtc, int? Minutes) Range(RecentWindow window, DateTime nowLocal, DateTime? customFrom, DateTime? customTo)
    {
        if (window.Minutes is { } minutes) return (nowLocal.ToUniversalTime().AddMinutes(-minutes), null, minutes);
        if (window.StartLocal is { } start) return (DateTime.SpecifyKind(start(nowLocal), DateTimeKind.Local).ToUniversalTime(), null, null);
        var from = customFrom ?? nowLocal.AddMinutes(-RecentFiles.DefaultMinutes);
        var to = customTo is { } end && end > from ? end : (DateTime?)null;
        return (DateTime.SpecifyKind(from, DateTimeKind.Local).ToUniversalTime(),
            to is { } until ? DateTime.SpecifyKind(until, DateTimeKind.Local).ToUniversalTime() : null, null);
    }

    /// <summary>True when a found entry is of a ticked kind.</summary>
    public static bool Wanted(RecentEntry entry, IEnumerable<FileTypeGroup> groups, IReadOnlyCollection<string> kinds) =>
        entry.IsFolder ? kinds.Contains(FileTypes.Folders) : kinds.Contains(Kind(entry, groups));

    public static string Kind(RecentEntry entry, IEnumerable<FileTypeGroup> groups) =>
        entry.IsFolder ? FileTypes.Folders : FileTypes.KindOf(entry.Path, groups);

    /// <summary>The name a watched folder gets on this PC: Desktop and Documents for their tokens, otherwise the folder's own name.</summary>
    public static string FolderLabel(string root)
    {
        var known = CollectFolders.OnStudentComputer.FirstOrDefault(choice => choice.Value.Equals(root, StringComparison.OrdinalIgnoreCase));
        if (known.Value is not null) return known.Value.Equals("%USERPROFILE%", StringComparison.OrdinalIgnoreCase) ? "Profile" : known.Name;
        var trimmed = root.Trim().TrimEnd('\\', '/');
        var name = trimmed.Length == 0 ? "" : Path.GetFileName(trimmed);
        if (name.Length == 0) name = trimmed.Replace(":", "", StringComparison.Ordinal);
        return Safe(name.Trim('%'), "Folder");
    }

    /// <summary>The folder of one computer under the destination: the computer's name, and the student's when someone is signed in.</summary>
    public static string ComputerFolder(string destination, ClientInfo client) =>
        Path.Combine(destination, Safe(client.User.Length > 0 ? $"{client.Name}_{client.User}" : client.Name, "Computer"));

    /// <summary>Where a found file is copied to, or null when its path would leave the destination.</summary>
    public static string? LocalPath(string destination, ClientInfo client, RecentEntry entry, string? insideFolder = null)
    {
        var relative = insideFolder is null ? entry.RelativePath : Path.Combine(insideFolder, entry.RelativePath);
        if (!RecentFiles.SafeRelativePath(relative)) return null;
        var root = Path.Combine(ComputerFolder(destination, client), FolderLabel(entry.Root));
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>The entries to copy, without those inside a folder that is copied as well (it brings them anyway).</summary>
    public static List<RecentEntry> WithoutNested(IEnumerable<RecentEntry> entries)
    {
        var all = entries.ToList();
        var folders = all.Where(entry => entry.IsFolder).Select(entry => entry.Path.TrimEnd('\\') + "\\").ToList();
        return all.Where(entry => !folders.Any(folder => entry.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>
    /// True when a file is copied: there is no copy yet, or (overwriting) the copy differs in size or change time. An unchanged copy is
    /// left alone either way, so copying again does not send the same file twice.
    /// </summary>
    public static bool NeedsCopy(RecentEntry entry, LocalFile? copy, bool overwrite) =>
        copy is null || overwrite && (copy.Size != entry.Size || Math.Abs((copy.Modified - entry.ModifiedUtc).TotalSeconds) >= 1);

    /// <summary>The copies whose time is up: copied more than <paramref name="keepDays"/> days ago. None when copies are kept.</summary>
    public static List<RecentCopyRecord> Expired(IEnumerable<RecentCopyRecord> records, DateTime nowUtc, int keepDays) =>
        keepDays <= 0 ? [] : records.Where(record => record.CopiedUtc < nowUtc.AddDays(-keepDays)).ToList();

    /// <summary>
    /// True when an expired copy may be deleted: it still has the change time it was copied with, so nobody has worked on it since.
    /// A copy someone opened and saved here is kept.
    /// </summary>
    public static bool MayDelete(RecentCopyRecord record, DateTime? currentModifiedUtc) =>
        currentModifiedUtc is { } now && Math.Abs((now - record.ModifiedUtc).TotalSeconds) < 1;

    /// <summary>Notes a copy; a file copied again replaces its old record, and the oldest records go past <see cref="MaxRecords"/>.</summary>
    public static void Remember(RecentFilesSettings settings, string localPath, DateTime copiedUtc, DateTime modifiedUtc)
    {
        settings.Copies.RemoveAll(record => record.LocalPath.Equals(localPath, StringComparison.OrdinalIgnoreCase));
        settings.Copies.Add(new RecentCopyRecord { LocalPath = localPath, CopiedUtc = copiedUtc, ModifiedUtc = modifiedUtc });
        if (settings.Copies.Count > MaxRecords) settings.Copies.RemoveRange(0, settings.Copies.Count - MaxRecords);
    }

    /// <summary>A file or folder name made of the characters Windows allows; <paramref name="fallback"/> when nothing is left.</summary>
    public static string Safe(string name, string fallback)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = string.Concat(name.Select(ch => bad.Contains(ch) ? '_' : ch)).Trim().TrimEnd('.');
        return clean.Length == 0 || clean.Trim('_').Length == 0 ? fallback : clean;
    }

    /// <summary>The destination when none was chosen: Recent files in this Windows user's Documents.</summary>
    public static string DefaultDestination() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Recent files");
}
