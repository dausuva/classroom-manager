// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;

namespace ClassroomManager.Host;

/// <summary>
/// What the Send files dialog shows and records, decided without a window (T-253 phase 2): the summary of the chosen files, the review lines, the warning, the title,
/// and the order the finished deliveries are written to the transfer history. The dialog and the delivery itself (<see cref="FileTransferWorkflow"/>) stay as they were.
/// </summary>
public sealed class FileSendPlan
{
    const int PreviewLines = 12;

    public int Count { get; }
    public long TotalBytes { get; }
    public string Preview { get; }
    public string Destination { get; }
    public bool Overwrite { get; }

    FileSendPlan(IReadOnlyList<(string Name, long Size)> files, string destination, bool overwrite)
    {
        Count = files.Count;
        TotalBytes = files.Sum(file => file.Size);
        Preview = string.Join("\n", files.Take(PreviewLines).Select(file => $"• {file.Name} ({StagedUploadsWindow.FormatSize(file.Size)})")) + (files.Count > PreviewLines ? "\n• ..." : "");
        Destination = destination;
        Overwrite = overwrite;
    }

    public static FileSendPlan Build(IReadOnlyList<(string Name, long Size)> files, string destination, bool overwrite) => new(files, destination, overwrite);

    public string Title => $"Send {Count} file(s) to {Destination}";

    public string Warning => Overwrite ? "A file with the same name at the destination can be replaced." : "";

    public IReadOnlyList<BulkOperationReviewItem> ReviewItems =>
    [
        new("Files", $"{Count}, {StagedUploadsWindow.FormatSize(TotalBytes)}\n{Preview}"),
        new("Destination", Destination),
        new("Existing files", Overwrite ? "Overwrite files with the same name" : "Keep existing files"),
    ];

    /// <summary>The deliveries as the history lists them: by computer, then by file name, in any case.</summary>
    public static List<FileTransferRecord> HistoryOrder(IEnumerable<FileTransferRecord> records) =>
        records.OrderBy(record => record.Computer, StringComparer.OrdinalIgnoreCase).ThenBy(record => record.FileName, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>What the Collect files dialog asked for, decided without a window: where on this computer the files go, and the pattern.</summary>
public sealed class CollectPlan
{
    public string? Problem { get; }

    /// <summary>The folder the teacher chose (what is remembered for next time), before any dated subfolder.</summary>
    public string ChosenFolder { get; }

    /// <summary>Where the files are saved: the chosen folder, or a subfolder of it named by the date and time.</summary>
    public string LocalRoot { get; }
    public string StudentFolder { get; }
    public string Pattern { get; }
    public bool Recursive { get; }
    public bool Overwrite { get; }

    CollectPlan(string local, bool datedSubfolder, DateTime now, string studentFolder, string pattern, bool recursive, bool overwrite)
    {
        ChosenFolder = local;
        Problem = Path.IsPathFullyQualified(local) ? null : "Choose a folder to save into.";
        LocalRoot = datedSubfolder && Problem is null ? Path.Combine(local, now.ToString("yyyy-MM-dd_HHmm")) : local;
        StudentFolder = studentFolder;
        Pattern = pattern.Length > 0 ? pattern : "*.*";
        Recursive = recursive;
        Overwrite = overwrite;
    }

    public static CollectPlan Build(string local, bool datedSubfolder, DateTime now, string studentFolder, string pattern, bool recursive, bool overwrite) =>
        new(local, datedSubfolder, now, studentFolder, pattern, recursive, overwrite);
}

/// <summary>The decisions of the "copy new files" quick action (Sync): the pattern, what a computer answered, and the status line.</summary>
public static class FolderSyncPlan
{
    public static string Pattern(CustomQuickAction action) => action.Arguments.Length > 0 ? action.Arguments : "*.*";

    /// <summary>The files a computer lists (<c>fs.find</c>), with their paths relative to the folder searched.</summary>
    public static (string RemoteFolder, List<RemoteFile> Files) ReadFound(JsonObject found)
    {
        var remoteFolder = found["folder"]!.GetValue<string>();
        var files = found["files"]!.AsArray()
            .Select(file => new RemoteFile(
                Path.GetRelativePath(remoteFolder, file!["path"]!.GetValue<string>()),
                file["size"]?.GetValue<long>() ?? -1,
                FolderSync.ParseModified(file["modified"]?.GetValue<string>() ?? "")))
            .ToList();
        return (remoteFolder, files);
    }

    public static string Status(int copied, int total, int failed) => failed == 0
        ? $"Synced: {copied} of {total} file(s) copied"
        : $"Synced: {copied} of {total} file(s) copied, {failed} could not be copied";
}
