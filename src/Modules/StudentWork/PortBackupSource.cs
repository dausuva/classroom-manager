// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.Text.Json.Nodes;
using ClassroomManager.Modules;

namespace ClassroomManager.StudentWork;

/// <summary>
/// A backup source that works only through the module's ports: it asks one computer what changed (<c>client.call</c>) and has each file
/// copied into the "backup" sink (<c>client.transfer</c>). It never sees a connection, an address, a destination path or a file's bytes.
/// </summary>
/// <param name="client">The run's connection; the Host checks every call and transfer.</param>
/// <param name="computer">The one computer being backed up.</param>
/// <param name="folderLabels">The name each watched folder gets inside the computer's backup folder, as the Host chose them.</param>
public sealed class PortBackupSource(IModuleClient client, ModuleComputer computer, IReadOnlyDictionary<string, string> folderLabels) : IBackupSource
{
    public const string Sink = "backup";
    public const string ScanCommand = "fs.recent";

    /// <summary>The start of "everything" when a whole folder is copied.</summary>
    static readonly DateTime Always = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    const int FolderMaxItems = 5000;

    public async Task<BackupScan> ScanAsync(BackupQuery query, CancellationToken cancellation)
    {
        var arguments = new JsonObject
        {
            ["folders"] = new JsonArray(query.Folders.Select(folder => (JsonNode)folder).ToArray()),
            ["sinceUtc"] = Utc(query.SinceUtc),
            ["extensions"] = new JsonArray(),
            ["allFiles"] = true,
            ["includeFolders"] = false,
            ["maxItems"] = query.MaxItems,
            ["afterPath"] = query.AfterPath,
        };
        if (query.AfterModifiedUtc is { } after) arguments["afterModifiedUtc"] = Utc(after);
        return ReadScan(await client.CallAsync(computer, ScanCommand, arguments.ToJsonString(), cancellation));
    }

    public async Task<BackupCopyOutcome> CopyAsync(IReadOnlyList<BackupEntry> entries, CancellationToken cancellation)
    {
        var failed = new List<string>();
        var files = new List<(BackupEntry Entry, string? Destination)>();
        foreach (var entry in entries)
        {
            if (!entry.IsFolder) { files.Add((entry, Destination(entry.Root, entry.RelativePath))); continue; }
            var inside = await ScanFolderAsync(entry.Path, cancellation);
            if (inside.Missing.Count > 0) failed.Add($"{entry.Path}: the folder no longer exists.");
            if (inside.Truncated) failed.Add($"{entry.Path}: only the newest {inside.Items.Count} files were copied.");
            foreach (var file in inside.Items)
                files.Add((file with { Root = entry.Root }, Destination(entry.Root, entry.RelativePath + "\\" + file.RelativePath)));
        }

        int copied = 0, upToDate = 0;
        var unique = files.GroupBy(file => file.Destination ?? file.Entry.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        foreach (var (entry, destination) in unique)
        {
            if (destination is null) { failed.Add($"{entry.Path}: its name would leave the destination folder, so it was not copied."); continue; }
            try
            {
                var result = await client.TransferAsync(computer, new ModuleTransfer(entry.Path, Sink, destination, entry.Size, entry.ModifiedUtc, Overwrite: true), cancellation);
                if (result.Copied) copied++; else upToDate++;
            }
            // One file the computer cannot read or this PC cannot write is named and the rest go on; a lost connection or a refused call ends the run.
            catch (ModuleTransferException ex) { failed.Add($"{entry.Path}: {ex.Message}"); }
        }
        return new(copied, upToDate, failed);
    }

    Task<BackupScan> ScanFolderAsync(string folder, CancellationToken cancellation) => ScanAsync(new([folder], Always, FolderMaxItems), cancellation);

    /// <summary>Where a file goes below the computer's folder: the watched folder's label and its path under it. Null when it would not be safe.</summary>
    string? Destination(string root, string relative)
    {
        if (!folderLabels.TryGetValue(root, out var label) || !IsPlainRelative(relative)) return null;
        var destination = label + "\\" + relative;
        return ModulePaths.Problem(destination) is null ? destination : null;
    }

    static bool IsPlainRelative(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) && !path.Split('\\', '/').Any(part => part == "..");

    static string Utc(DateTime time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    static BackupScan ReadScan(string answer)
    {
        var data = JsonNode.Parse(answer) as JsonObject ?? [];
        bool Flag(string key) => data[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        IReadOnlyList<string> Texts(string key, bool dropEmpty) => (data[key] as JsonArray ?? [])
            .Select(node => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "").Where(text => !dropEmpty || text.Length > 0).ToList();
        return new((data["items"] as JsonArray ?? []).Select(ReadEntry).OfType<BackupEntry>().ToList(), Flag("truncated"), Texts("missing", dropEmpty: true),
            Flag("supportsPaging"), Flag("hasMore"), Texts("errors", dropEmpty: false));
    }

    // An entry that is malformed, has no path or names a path that leaves its folder is left out, as the Host's own reader does.
    static BackupEntry? ReadEntry(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        string Text(string key) => o[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
        var relative = Text("relativePath");
        if (Text("path").Length == 0 || !IsPlainRelative(relative)) return null;
        if (!DateTime.TryParse(Text("modifiedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var modified)) return null;
        var size = o["size"] is JsonValue sizeValue && sizeValue.TryGetValue<long>(out var bytes) ? Math.Max(0, bytes) : 0;
        var isFolder = o["isFolder"] is JsonValue folderValue && folderValue.TryGetValue<bool>(out var folder) && folder;
        return new BackupEntry(Text("root"), Text("path"), relative, isFolder, size, DateTime.SpecifyKind(modified, DateTimeKind.Utc));
    }
}
