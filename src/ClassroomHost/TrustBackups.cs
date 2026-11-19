// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace ClassroomManager.Host;

/// <summary>A copy of the Host settings made just before a change to the approvals.</summary>
public sealed record TrustBackup(string Kind, string Path, DateTime At)
{
    public string Describe() => $"{At:yyyy-MM-dd HH:mm}  {Kind}  {Path}";
}

/// <summary>
/// The backups that <c>trust-check --fix</c>, <c>--from-files</c>, a restore and ClassroomTrustMigration make, and how to put one
/// back. A restore changes only the approved certificates (and, if asked, the list of computers): everything else in the current
/// settings, such as newer rooms or schedules, stays. Keys are never touched, because no approval change ever writes them.
/// </summary>
public static class TrustBackups
{
    static readonly (string Pattern, string Kind)[] Files =
    [
        (".trust-fix-*.bak", "trust-check --fix"),
        (".trust-enrol-*.bak", "trust-check --from-files"),
        (".before-restore-*.bak", "before an earlier restore"),
    ];

    /// <summary>Newest first.</summary>
    public static List<TrustBackup> List(string settingsPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        var name = Path.GetFileName(settingsPath);
        var found = new List<TrustBackup>();
        if (!Directory.Exists(directory)) return found;
        foreach (var (pattern, kind) in Files)
            foreach (var file in Directory.EnumerateFiles(directory, name + pattern))
                found.Add(new TrustBackup(kind, file, StampOf(file) ?? File.GetLastWriteTime(file)));
        foreach (var folder in Directory.EnumerateDirectories(directory, "trust-migration-backup-*"))
            if (File.Exists(Path.Combine(folder, name)))
                found.Add(new TrustBackup("trust migration import", folder, Directory.GetCreationTime(folder)));
        return found.OrderByDescending(backup => backup.At).ToList();
    }

    static DateTime? StampOf(string file)
    {
        var text = Path.GetFileNameWithoutExtension(file);
        var stamp = text[(text.LastIndexOf('-', text.LastIndexOf('-') - 1) + 1)..];
        return DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
    }

    /// <summary>The settings file inside a backup: the file itself, or <c>host.json</c> in a backup folder.</summary>
    public static string SettingsFileOf(TrustBackup backup, string settingsPath) =>
        Directory.Exists(backup.Path) ? Path.Combine(backup.Path, Path.GetFileName(settingsPath)) : backup.Path;

    /// <summary>A backup chosen by its number in <see cref="List"/> (1 is the newest) or by its path.</summary>
    public static TrustBackup Find(string settingsPath, string choice)
    {
        var all = List(settingsPath);
        if (int.TryParse(choice, out var number))
            return number >= 1 && number <= all.Count ? all[number - 1] : throw new CliUsageException($"There is no backup number {number}; there are {all.Count}.");
        var full = Path.GetFullPath(choice.Trim().Trim('"'));
        return all.FirstOrDefault(backup => Path.GetFullPath(backup.Path).Equals(full, StringComparison.OrdinalIgnoreCase))
               ?? throw new CliUsageException($"Not a backup made by these tools: {choice}");
    }

    static string Key(JsonNode? entry) => $"{entry?["address"]?.GetValue<string>()?.Trim().ToLowerInvariant()}|{entry?["port"]?.ToString()}|{entry?["mac"]?.GetValue<string>()}";
    static string Pins(JsonNode? entry) => string.Join(",", (entry?["certificateSha256"] as JsonArray ?? []).Select(pin => pin?.GetValue<string>()?.Substring(0, Math.Min(16, pin.GetValue<string>().Length))).Order());
    static JsonArray Array(JsonObject root, string name) => root.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value as JsonArray ?? [];

    /// <summary>What a restore would change, in plain lines. Empty when the backup and the current settings agree.</summary>
    public static List<string> Compare(string currentPath, string backupSettingsPath, bool includeComputers)
    {
        var current = JsonNode.Parse(File.ReadAllText(currentPath)) as JsonObject ?? throw new InvalidDataException("The current settings are not a JSON object.");
        var backup = JsonNode.Parse(File.ReadAllText(backupSettingsPath)) as JsonObject ?? throw new InvalidDataException("The backup is not a JSON object.");
        var lines = new List<string>();

        var now = Array(current, "trustedTlsEndpoints").ToDictionary(Key, entry => entry);
        var then = Array(backup, "trustedTlsEndpoints").ToDictionary(Key, entry => entry);
        string Show(JsonNode? entry) => $"{entry?["address"]} port {entry?["port"]}{(string.IsNullOrEmpty(entry?["mac"]?.GetValue<string>()) ? "" : " card " + ComputerIdentity.FormatMac(entry!["mac"]!.GetValue<string>()))} certificate {Pins(entry)}...";
        foreach (var (key, entry) in then.Where(pair => !now.ContainsKey(pair.Key)))
            lines.Add("Approval brought back: " + Show(entry));
        foreach (var (key, entry) in now.Where(pair => !then.ContainsKey(pair.Key)))
            lines.Add("Approval removed again: " + Show(entry));
        foreach (var (key, entry) in then.Where(pair => now.TryGetValue(pair.Key, out var other) && Pins(other) != Pins(pair.Value)))
            lines.Add($"Certificate changed back: {Show(entry)} (now {Pins(now[key])}...)");

        if (includeComputers)
        {
            string Computer(JsonNode? c) => $"{c?["name"]} at {c?["address"]}";
            var nowComputers = Array(current, "computers").ToDictionary(c => c?["clientId"]?.GetValue<string>() is { Length: > 0 } id ? id : Computer(c), c => c);
            var thenComputers = Array(backup, "computers").ToDictionary(c => c?["clientId"]?.GetValue<string>() is { Length: > 0 } id ? id : Computer(c), c => c);
            foreach (var (key, c) in thenComputers.Where(pair => !nowComputers.ContainsKey(pair.Key))) lines.Add("Computer brought back: " + Computer(c));
            foreach (var (key, c) in nowComputers.Where(pair => !thenComputers.ContainsKey(pair.Key))) lines.Add("Computer removed again: " + Computer(c));
            foreach (var (key, c) in thenComputers.Where(pair => nowComputers.TryGetValue(pair.Key, out var other) && other?["address"]?.ToString() != pair.Value?["address"]?.ToString()))
                lines.Add($"Computer address changed back: {c?["name"]} from {nowComputers[key]?["address"]} to {c?["address"]}");
        }
        return lines;
    }

    /// <summary>
    /// Puts the backup's approvals (and optionally its computers) into the current settings, after a copy of the current settings.
    /// Returns the path of that copy. Everything else in the current settings is kept as it is, unknown fields included.
    /// </summary>
    public static string Restore(string settingsPath, string backupSettingsPath, bool includeComputers, DateTime now)
    {
        var current = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? throw new InvalidDataException("The current settings are not a JSON object.");
        var backup = JsonNode.Parse(File.ReadAllText(backupSettingsPath)) as JsonObject ?? throw new InvalidDataException("The backup is not a JSON object.");

        var safety = settingsPath + ".before-restore-" + now.ToString("yyyyMMdd-HHmmss") + ".bak";
        File.Copy(settingsPath, safety);

        void Replace(string name)
        {
            foreach (var existing in current.Select(pair => pair.Key).Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList()) current.Remove(existing);
            current[name] = Array(backup, name).DeepClone();
        }
        Replace("trustedTlsEndpoints");
        if (includeComputers) Replace("computers");

        var temporary = settingsPath + ".restore.tmp";
        File.WriteAllText(temporary, current.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Replace(temporary, settingsPath, null);
        return safety;
    }
}
