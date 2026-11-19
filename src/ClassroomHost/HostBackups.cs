// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ClassroomManager.Host;

/// <summary>One earlier copy of the Host settings and keys, as shown in the Backups dialog.</summary>
public sealed record HostBackup(string Folder, DateTime TakenUtc, string Reason, int Computers, IReadOnlyList<string> Rooms, IReadOnlyList<string> KeyNames, bool HasKeyFile)
{
    public string Label =>
        $"{TakenUtc.ToLocalTime():yyyy-MM-dd HH:mm}  -  {HostBackups.ReasonText(Reason)}  -  {Computers} computer(s)" +
        (Rooms.Count > 0 ? $" in {string.Join(", ", Rooms.Take(4))}{(Rooms.Count > 4 ? ", ..." : "")}" : "") +
        (HasKeyFile ? $", keys: {(KeyNames.Count > 0 ? string.Join(", ", KeyNames.Take(4)) : "stored")}" : ", no keys");
}

/// <summary>
/// Keeps earlier generations of the Host's settings (host.json) and keys (host.keys) in a
/// "backups" folder beside them, so one bad write is never final. A generation is taken when the
/// Host starts, before settings are imported or restored, and before a save that would empty a
/// Host that has computers. The key file is copied as it is: it stays protected for the same
/// Windows account and is useless to anyone else.
/// </summary>
public static class HostBackups
{
    /// <summary>How many generations are kept; the oldest go first.</summary>
    public const int Keep = 30;

    public const string Start = "start";
    public const string BeforeEmptying = "before-emptying";
    public const string BeforeImport = "before-import";
    public const string BeforeRestore = "before-restore";
    public const string BeforeOverwrite = "before-overwrite";
    public const string BeforeKeyChange = "before-key-change";

    public static string ReasonText(string reason) => reason switch
    {
        Start => "when the Host started",
        BeforeEmptying => "before the settings were emptied",
        BeforeOverwrite => "before settings another program had changed were written over",
        BeforeImport => "before a configuration was imported",
        BeforeRestore => "before a backup was restored",
        BeforeKeyChange => "before the lab key was changed",
        _ => reason,
    };

    public static string FolderFor(string settingsPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "backups");

    static string KeysPath(string settingsPath) => Path.ChangeExtension(settingsPath, ".keys");

    /// <summary>
    /// Copies the current settings and keys into a new generation. Returns its folder, or null when
    /// there is nothing to copy or nothing has changed since the newest generation.
    /// </summary>
    public static string? Snapshot(string settingsPath, string reason, DateTime? nowUtc = null)
    {
        if (!File.Exists(settingsPath)) return null;
        var settings = File.ReadAllBytes(settingsPath);
        var keysPath = KeysPath(settingsPath);
        var keys = File.Exists(keysPath) ? File.ReadAllBytes(keysPath) : null;

        var newest = List(settingsPath).FirstOrDefault();
        if (newest is not null && Same(Path.Combine(newest.Folder, "host.json"), settings) && Same(Path.Combine(newest.Folder, "host.keys"), keys))
            return null;

        var now = nowUtc ?? DateTime.UtcNow;
        var root = FolderFor(settingsPath);
        var folder = Path.Combine(root, $"{now:yyyyMMdd-HHmmss}-{reason}");
        for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(root, $"{now:yyyyMMdd-HHmmss}-{reason}-{n}");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "host.json"), settings);
        if (keys is not null) File.WriteAllBytes(Path.Combine(folder, "host.keys"), keys);

        foreach (var old in List(settingsPath).Skip(Keep))
            try { Directory.Delete(old.Folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return folder;
    }

    static bool Same(string path, byte[]? content) =>
        content is null ? !File.Exists(path) : File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content);

    /// <summary>The generations, newest first. A folder that cannot be read is left out.</summary>
    public static List<HostBackup> List(string settingsPath)
    {
        var root = FolderFor(settingsPath);
        if (!Directory.Exists(root)) return [];
        var found = new List<HostBackup>();
        foreach (var folder in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(folder);
            if (name.Length < 16 || !DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var taken)) continue;
            var file = Path.Combine(folder, "host.json");
            if (!File.Exists(file)) continue;
            var (computers, rooms, keyNames) = Summary(file);
            var reason = name[16..];
            // "start-2" is a second generation taken within the same second.
            var dash = reason.LastIndexOf('-');
            if (dash > 0 && int.TryParse(reason[(dash + 1)..], out _)) reason = reason[..dash];
            found.Add(new HostBackup(folder, taken, reason, computers, rooms, keyNames, File.Exists(Path.Combine(folder, "host.keys"))));
        }
        return found.OrderByDescending(backup => backup.TakenUtc).ThenByDescending(backup => backup.Folder, StringComparer.Ordinal).ToList();
    }

    /// <summary>What a settings file holds, read without loading it as settings (loading can write).</summary>
    public static (int Computers, List<string> Rooms, List<string> KeyNames) Summary(string settingsFile)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsFile));
            var root = document.RootElement;
            var rooms = new List<string>();
            var keyNames = new List<string>();
            int computers = 0;
            if (Property(root, "computers") is { ValueKind: JsonValueKind.Array } list)
                foreach (var computer in list.EnumerateArray())
                {
                    computers++;
                    var room = Property(computer, "room") is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";
                    if (room.Length > 0 && !rooms.Contains(room, StringComparer.OrdinalIgnoreCase)) rooms.Add(room);
                }
            if (Property(root, "defaultKeyName") is { ValueKind: JsonValueKind.String } main && main.GetString() is { Length: > 0 } mainName) keyNames.Add(mainName);
            if (Property(root, "additionalHostKeys") is { ValueKind: JsonValueKind.Array } more)
                foreach (var key in more.EnumerateArray())
                    if (Property(key, "name") is { ValueKind: JsonValueKind.String } name && name.GetString() is { Length: > 0 } text) keyNames.Add(text);
            return (computers, rooms, keyNames);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return (0, [], []); }
    }

    static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    /// <summary>
    /// True when saving <paramref name="next"/> would replace settings that list computers with
    /// settings that have no computers and no keys: what a mistake looks like, not what a person does.
    /// </summary>
    public static bool WouldEmpty(string settingsPath, HostSettings next)
    {
        if (next.Computers.Count > 0 || next.HasHostKeys || !File.Exists(settingsPath)) return false;
        return Summary(settingsPath).Computers > 0;
    }

    /// <summary>
    /// Adds a line to settings-writes.log beside the settings: when, which program, and what the file now
    /// holds. When settings go missing, this names the program that wrote last. The log keeps its newest part.
    /// </summary>
    public static void Journal(string settingsPath, string what)
    {
        try
        {
            var log = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "settings-writes.log");
            if (File.Exists(log) && new FileInfo(log).Length > 256 * 1024)
            {
                var lines = File.ReadAllLines(log);
                File.WriteAllLines(log, lines.Skip(lines.Length / 2));
            }
            var (computers, rooms, keys) = Summary(settingsPath);
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            File.AppendAllText(log,
                // Only the first two arguments ("--cli update"): later ones can hold message texts.
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {what}  by {Environment.ProcessPath} {string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Take(2))} (pid {process.Id})" +
                $"  now: {computers} computer(s), {rooms.Count} room(s), keys: {string.Join(", ", keys)}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Puts a generation back as the current settings and keys, after copying the current ones into a generation of their own.</summary>
    public static void Restore(string settingsPath, HostBackup backup)
    {
        var settings = File.ReadAllBytes(Path.Combine(backup.Folder, "host.json"));
        var keysFile = Path.Combine(backup.Folder, "host.keys");
        var keys = File.Exists(keysFile) ? File.ReadAllBytes(keysFile) : null;
        Snapshot(settingsPath, BeforeRestore);
        AtomicFile.WriteAllBytes(settingsPath, settings);
        if (keys is not null) AtomicFile.WriteAllBytes(KeysPath(settingsPath), keys);
        else if (File.Exists(KeysPath(settingsPath))) File.Delete(KeysPath(settingsPath));
    }
}
