// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.Text.Json.Nodes;

namespace ClassroomManager.Shared;

/// <summary>A named group of file extensions the Host filters recent files by ("Documents": .docx, .pdf ...).</summary>
public sealed record FileTypeGroup(string Name, IReadOnlyList<string> Extensions);

/// <summary>
/// The file type groups of Recent files (T-212). The Host keeps an editable copy; these are the defaults and the rules for
/// reading an extension list someone typed.
/// </summary>
public static class FileTypes
{
    public const string Documents = "Documents";
    public const string Images = "Images";
    public const string SourceCode = "Source code";
    /// <summary>Folders changed in the time window. Not an extension group: the Client lists folders only when asked.</summary>
    public const string Folders = "Folders";
    /// <summary>Files whose extension is in no group.</summary>
    public const string Other = "Other files";

    public static IReadOnlyList<FileTypeGroup> Defaults { get; } =
    [
        new(Documents, [".doc", ".docx", ".docm", ".odt", ".rtf", ".txt", ".md", ".pdf", ".xls", ".xlsx", ".xlsm", ".ods", ".csv",
            ".ppt", ".pptx", ".odp", ".pub", ".vsdx"]),
        new(Images, [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".svg", ".ico", ".psd", ".xcf", ".kra"]),
        new(SourceCode, [".cs", ".py", ".java", ".js", ".ts", ".jsx", ".tsx", ".html", ".htm", ".css", ".scss", ".json", ".xml", ".yaml", ".yml",
            ".c", ".cpp", ".cc", ".h", ".hpp", ".go", ".rs", ".rb", ".php", ".sql", ".sh", ".ps1", ".bat", ".cmd", ".ipynb", ".kt", ".swift",
            ".lua", ".pas", ".dart", ".vb", ".sb3", ".ino"]),
    ];

    /// <summary>".docx" from "docx", "*.DOCX" or " .docx "; empty when it is not an extension (a path, a space or a wildcard inside).</summary>
    public static string NormalizeExtension(string? text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        if (value.StartsWith("*.", StringComparison.Ordinal)) value = value[1..];
        if (value.Length > 0 && value[0] != '.') value = "." + value;
        if (value.Length < 2 || value.Length > 20) return "";
        foreach (var ch in value[1..])
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')) return "";
        return value;
    }

    /// <summary>The extensions of a typed list ("docx, .pdf; *.txt"), each once, in the order given; anything that is not an extension is left out.</summary>
    public static List<string> ParseExtensions(string? text) =>
        (text ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeExtension).Where(ext => ext.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The group a file belongs to: the first group that lists its extension, otherwise <see cref="Other"/>.</summary>
    public static string KindOf(string fileName, IEnumerable<FileTypeGroup> groups)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext.Length > 0)
            foreach (var group in groups)
                if (group.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return group.Name;
        return Other;
    }
}

/// <summary>What <see cref="Cmd.FsRecent"/> is asked for: where to look, which time window, and which files.</summary>
public sealed record RecentQuery
{
    /// <summary>Folders as the Host names them: a token (%DESKTOP%) or a full path. The Client expands them.</summary>
    public IReadOnlyList<string> Folders { get; init; } = RecentFiles.DefaultFolders;
    public DateTime SinceUtc { get; init; }
    /// <summary>
    /// "The last N minutes", counted on the Client's own clock, so a Client whose clock is a few minutes off still lists what was just
    /// saved. Sent with <see cref="SinceUtc"/>; a Client uses this one when it is set.
    /// </summary>
    public int? WithinMinutes { get; init; }
    /// <summary>The end of the window; null means now.</summary>
    public DateTime? UntilUtc { get; init; }
    /// <summary>Extensions to list (".docx"); ignored when <see cref="AllFiles"/>.</summary>
    public IReadOnlyCollection<string> Extensions { get; init; } = [];
    /// <summary>List every file, whatever its extension.</summary>
    public bool AllFiles { get; init; }
    /// <summary>Also list folders changed in the window.</summary>
    public bool IncludeFolders { get; init; }
    public int MaxItems { get; init; } = RecentFiles.DefaultMaxItems;
    /// <summary>Exclusive cursor in newest-first, then case-insensitive path order (T-215).</summary>
    public DateTime? AfterModifiedUtc { get; init; }
    public string AfterPath { get; init; } = "";

    /// <summary>The arguments of <see cref="Cmd.FsRecent"/>. Times go as UTC, so the two computers' time zones do not matter.</summary>
    public JsonObject ToArgs() => new()
    {
        ["folders"] = new JsonArray(Folders.Select(folder => (JsonNode)folder).ToArray()),
        ["sinceUtc"] = SinceUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["withinMinutes"] = WithinMinutes,
        ["untilUtc"] = UntilUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["extensions"] = new JsonArray(Extensions.Select(ext => (JsonNode)ext).ToArray()),
        ["allFiles"] = AllFiles,
        ["includeFolders"] = IncludeFolders,
        ["maxItems"] = MaxItems,
        ["afterModifiedUtc"] = AfterModifiedUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["afterPath"] = AfterPath,
    };

    /// <summary>
    /// Reads the arguments back on the Client. "withinMinutes" counts back from the Client's own clock, for callers that do not want to
    /// depend on the two clocks agreeing; otherwise "sinceUtc" is used, and without either the default window (30 minutes) applies.
    /// </summary>
    public static RecentQuery FromArgs(JsonObject a, DateTime nowUtc)
    {
        var folders = (a["folders"] as JsonArray ?? [])
            .Select(node => node?.GetValue<string>()?.Trim() ?? "").Where(folder => folder.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(RecentFiles.MaxFolders).ToList();
        var since = a["withinMinutes"] is JsonValue minutes && minutes.TryGetValue<int>(out var within)
            ? nowUtc.AddMinutes(-Math.Clamp(within, 1, 60 * 24 * 366))
            : ReadUtc(a["sinceUtc"]) ?? nowUtc.AddMinutes(-RecentFiles.DefaultMinutes);
        return new RecentQuery
        {
            Folders = folders.Count > 0 ? folders : RecentFiles.DefaultFolders,
            SinceUtc = since,
            WithinMinutes = a["withinMinutes"] is JsonValue m && m.TryGetValue<int>(out var w) ? Math.Clamp(w, 1, 60 * 24 * 366) : null,
            UntilUtc = ReadUtc(a["untilUtc"]),
            AfterModifiedUtc = ReadUtc(a["afterModifiedUtc"]),
            AfterPath = a["afterPath"] is JsonValue cursor && cursor.TryGetValue<string>(out var afterPath) ? afterPath : "",
            Extensions = (a["extensions"] as JsonArray ?? []).Select(node => FileTypes.NormalizeExtension(node?.GetValue<string>()))
                .Where(ext => ext.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase),
            AllFiles = a["allFiles"] is JsonValue all && all.TryGetValue<bool>(out var allFiles) && allFiles,
            IncludeFolders = a["includeFolders"] is JsonValue dirs && dirs.TryGetValue<bool>(out var includeFolders) && includeFolders,
            MaxItems = a["maxItems"] is JsonValue max && max.TryGetValue<int>(out var maxItems)
                ? Math.Clamp(maxItems, 1, RecentFiles.MaxItemsLimit) : RecentFiles.DefaultMaxItems,
        };
    }

    static DateTime? ReadUtc(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) &&
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : null;

    /// <summary>True when an entry changed at <paramref name="modifiedUtc"/> falls in the window.</summary>
    public bool InWindow(DateTime modifiedUtc) => modifiedUtc >= SinceUtc && (UntilUtc is not { } until || modifiedUtc <= until);

    /// <summary>True when a file with this name is asked for (the window aside).</summary>
    public bool WantsFile(string fileName) =>
        AllFiles || Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
}

/// <summary>A file or folder found by <see cref="Cmd.FsRecent"/>.</summary>
/// <param name="Root">The watched folder it was found under, as the Host named it (%DESKTOP%).</param>
/// <param name="Path">Its full path on the Client.</param>
/// <param name="RelativePath">Its path under the watched folder.</param>
public sealed record RecentEntry(string Root, string Path, string RelativePath, bool IsFolder, long Size, DateTime ModifiedUtc)
{
    public JsonObject ToJson() => new()
    {
        ["root"] = Root, ["path"] = Path, ["relativePath"] = RelativePath, ["isFolder"] = IsFolder, ["size"] = Size,
        ["modifiedUtc"] = ModifiedUtc.ToString("O", CultureInfo.InvariantCulture),
    };

    /// <summary>Reads one entry of a reply; null when it is malformed or names a path that would leave its folder.</summary>
    public static RecentEntry? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        string Text(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        var relative = Text("relativePath");
        if (Text("path").Length == 0 || !RecentFiles.SafeRelativePath(relative)) return null;
        if (!DateTime.TryParse(Text("modifiedUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var modified)) return null;
        var size = o["size"] is JsonValue sv && sv.TryGetValue<long>(out var n) ? Math.Max(0, n) : 0;
        var isFolder = o["isFolder"] is JsonValue fv && fv.TryGetValue<bool>(out var f) && f;
        return new RecentEntry(Text("root"), Text("path"), relative, isFolder, size, DateTime.SpecifyKind(modified, DateTimeKind.Utc));
    }
}

/// <summary>The result of one scan: what was found (newest first), whether the list was cut short, and the folders that do not exist.</summary>
public sealed record RecentScan(IReadOnlyList<RecentEntry> Items, bool Truncated, IReadOnlyList<string> Missing)
{
    public bool SupportsPaging { get; init; }
    public bool HasMore { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public JsonObject ToJson() => new()
    {
        ["items"] = new JsonArray(Items.Select(item => (JsonNode)item.ToJson()).ToArray()),
        ["truncated"] = Truncated,
        ["missing"] = new JsonArray(Missing.Select(folder => (JsonNode)folder).ToArray()),
        ["supportsPaging"] = SupportsPaging,
        ["hasMore"] = HasMore,
        ["errors"] = new JsonArray(Errors.Select(error => (JsonNode)error).ToArray()),
    };

    public static RecentScan FromJson(JsonObject data) => new(
        (data["items"] as JsonArray ?? []).Select(RecentEntry.FromJson).OfType<RecentEntry>().ToList(),
        data["truncated"] is JsonValue t && t.TryGetValue<bool>(out var truncated) && truncated,
        (data["missing"] as JsonArray ?? []).Select(node => node?.GetValue<string>() ?? "").Where(folder => folder.Length > 0).ToList())
    {
        SupportsPaging = data["supportsPaging"] is JsonValue p && p.TryGetValue<bool>(out var paging) && paging,
        HasMore = data["hasMore"] is JsonValue m && m.TryGetValue<bool>(out var more) && more,
        Errors = (data["errors"] as JsonArray ?? []).Select(node => node?.GetValue<string>() ?? "").ToList(),
    };
}

/// <summary>
/// Recent files (T-212): the files the signed-in student changed in the watched folders. The Client only reads: it keeps no list and
/// no history; the Host asks when it wants to know.
/// </summary>
public static class RecentFiles
{
    /// <summary>The protocol from which a Client answers <see cref="Cmd.FsRecent"/>.</summary>
    public const string RequiredVersion = "1.19";

    public static IReadOnlyList<string> DefaultFolders { get; } = ["%DESKTOP%", "%DOCUMENTS%"];
    public const int DefaultMinutes = 30;
    public const int DefaultMaxItems = 1000;
    public const int MaxItemsLimit = 5000;
    public const int MaxFolders = 20;
    /// <summary>How deep under a watched folder the scan goes.</summary>
    public const int MaxDepth = 12;
    /// <summary>How many files and folders one scan looks at before it stops and says the list is cut short.</summary>
    public const int MaxVisited = 200_000;

    /// <summary>Folders that are never looked into: tools' caches and version control, which change constantly and hold no student work.</summary>
    static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".idea", ".vscode", "node_modules", "__pycache__", ".venv", "venv", ".gradle", ".cache",
    };

    /// <summary>A folder the scan does not look into.</summary>
    public static bool SkipFolder(string name) => SkippedFolders.Contains(name);

    /// <summary>A file that is not work: Office's and LibreOffice's lock files, temporary files and Explorer's own files.</summary>
    public static bool IsNoise(string fileName) =>
        fileName.StartsWith("~$", StringComparison.Ordinal) ||
        fileName.StartsWith(".~lock.", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase);

    /// <summary>A path that stays inside the folder it is joined to.</summary>
    public static bool SafeRelativePath(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) && !path.Split('\\', '/').Any(part => part == "..");

    // A cloud file that is not on this computer (OneDrive "online-only"): reading it would download it.
    const FileAttributes NotLocal = FileAttributes.Offline | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */ | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */;

    /// <summary>
    /// Looks through the watched folders for what changed in the query's window. Hidden and system entries, links to other folders,
    /// <see cref="SkipFolder"/> folders, <see cref="IsNoise"/> files and online-only cloud files are left out. A folder that cannot be
    /// read is skipped; a watched folder that does not exist is named in <see cref="RecentScan.Missing"/>.
    /// </summary>
    /// <param name="roots">Each watched folder as the Host named it, with its full path on this computer.</param>
    public static RecentScan Scan(IEnumerable<(string Name, string Path)> roots, RecentQuery query)
    {
        var found = new List<RecentEntry>();
        var missing = new List<string>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int visited = 0;
        bool cut = false;
        var options = new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };

        foreach (var (name, rootPath) in roots)
        {
            string full;
            try { full = Path.GetFullPath(rootPath).TrimEnd('\\', '/'); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { missing.Add(name); continue; }
            if (!Directory.Exists(full)) { missing.Add(name); continue; }
            // Documents inside a watched user folder, or the same folder named twice, is looked through once.
            if (!seen.Add(full)) continue;

            var pending = new Stack<(DirectoryInfo Dir, int Depth)>();
            pending.Push((new DirectoryInfo(full), 0));
            while (pending.Count > 0 && !cut)
            {
                var (dir, depth) = pending.Pop();
                IEnumerable<FileSystemInfo> children;
                try { children = dir.EnumerateFileSystemInfos("*", options).ToList(); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                { errors.Add($"{dir.FullName}: {ex.GetType().Name}"); continue; }

                foreach (var child in children)
                {
                    if (++visited > MaxVisited) { cut = true; break; }
                    try
                    {
                        var attributes = child.Attributes;
                        if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if (child is DirectoryInfo sub)
                        {
                            // Cloud folders can be reparse points without a link target; actual links are excluded.
                            if (sub.LinkTarget is not null || SkipFolder(sub.Name)) continue;
                            if (!seen.Add(sub.FullName)) continue;
                            if (query.IncludeFolders && query.InWindow(sub.LastWriteTimeUtc))
                                found.Add(new RecentEntry(name, sub.FullName, Path.GetRelativePath(full, sub.FullName), true, 0, sub.LastWriteTimeUtc));
                            if (depth + 1 < MaxDepth) pending.Push((sub, depth + 1));
                            else errors.Add($"{sub.FullName}: scan depth limit reached");
                        }
                        else if (child is FileInfo file)
                        {
                            if ((attributes & NotLocal) != 0 || file.LinkTarget is not null || IsNoise(file.Name) || !query.WantsFile(file.Name)) continue;
                            if (!query.InWindow(file.LastWriteTimeUtc)) continue;
                            found.Add(new RecentEntry(name, file.FullName, Path.GetRelativePath(full, file.FullName), false, file.Length, file.LastWriteTimeUtc));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    { errors.Add($"{child.FullName}: {ex.GetType().Name}"); }
                }
            }
        }

        var newest = found.Where(entry => query.AfterModifiedUtc is not { } after || entry.ModifiedUtc < after ||
                entry.ModifiedUtc == after && StringComparer.OrdinalIgnoreCase.Compare(entry.Path, query.AfterPath) > 0)
            .OrderByDescending(entry => entry.ModifiedUtc).ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToList();
        bool truncated = cut || newest.Count > query.MaxItems;
        if (cut) errors.Add("Scan entry limit reached; choose smaller watched folders.");
        return new RecentScan(newest.Take(query.MaxItems).ToList(), truncated, missing)
        { SupportsPaging = true, HasMore = newest.Count > query.MaxItems, Errors = errors };
    }
}
