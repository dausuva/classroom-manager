// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// The browser data of one Windows user on disk: Chrome and Edge under LocalAppData, Firefox under
/// AppData (profiles) and LocalAppData (cache). The two folders are passed in, so the same code runs
/// against a scratch folder in tests. Everything here assumes the browsers are closed; a file that
/// is still in use is counted as failed and left alone.
/// </summary>
sealed class BrowserDataStore
{
    readonly string _local;
    readonly string _roaming;

    public BrowserDataStore(string localAppData, string roamingAppData)
    {
        _local = localAppData;
        _roaming = roamingAppData;
    }

    /// <summary>Chrome and Edge keep the same layout in differently named folders.</summary>
    IEnumerable<(string Browser, string UserData)> ChromiumRoots()
    {
        yield return ("Chrome", Path.Combine(_local, "Google", "Chrome", "User Data"));
        yield return ("Edge", Path.Combine(_local, "Microsoft", "Edge", "User Data"));
    }

    string FirefoxRoot => Path.Combine(_roaming, "Mozilla", "Firefox");
    string FirefoxCacheRoot => Path.Combine(_local, "Mozilla", "Firefox");

    static bool IsChromiumProfileFolder(string name) =>
        name.Equals("Default", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase);

    // ---- clearing ---------------------------------------------------------------------------------

    static readonly string[] ChromiumHistory =
    [
        "History", "History-journal", "Visited Links", "Top Sites", "Top Sites-journal", "Shortcuts", "Shortcuts-journal",
        "Network Action Predictor", "Network Action Predictor-journal", "Current Session", "Current Tabs", "Last Session", "Last Tabs", "Sessions",
    ];
    static readonly string[] ChromiumCookies = [@"Network\Cookies", @"Network\Cookies-journal", "Cookies", "Cookies-journal"];
    static readonly string[] ChromiumCache = ["Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage"];

    // places.sqlite also holds the bookmarks; Firefox restores those from its own daily backup
    // (bookmarkbackups, which is left in place) the next time it starts.
    static readonly string[] FirefoxHistory =
    [
        "places.sqlite", "places.sqlite-wal", "places.sqlite-shm", "formhistory.sqlite", "sessionstore.jsonlz4", "sessionstore-backups",
    ];
    static readonly string[] FirefoxCookies = ["cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm"];

    public sealed record Cleared(long Files, long Bytes, long Failed);

    /// <summary>Removes the chosen kinds of browsing data from every profile of every browser.</summary>
    public Cleared Clear(bool history, bool cookies, bool cache)
    {
        long files = 0, bytes = 0, failed = 0;
        void Remove(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var size = new FileInfo(path).Length;
                    File.Delete(path);
                    files++;
                    bytes += size;
                }
                else if (Directory.Exists(path))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path)) Remove(entry);
                    Directory.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
        }

        foreach (var (_, userData) in ChromiumRoots())
        {
            if (!Directory.Exists(userData)) continue;
            foreach (var profile in Directory.EnumerateDirectories(userData).Where(folder => IsChromiumProfileFolder(Path.GetFileName(folder))))
            {
                if (history) foreach (var name in ChromiumHistory) Remove(Path.Combine(profile, name));
                if (cookies) foreach (var name in ChromiumCookies) Remove(Path.Combine(profile, name));
                if (cache) foreach (var name in ChromiumCache) Remove(Path.Combine(profile, name));
            }
        }
        foreach (var profile in Folders(Path.Combine(FirefoxRoot, "Profiles")))
        {
            if (history) foreach (var name in FirefoxHistory) Remove(Path.Combine(profile, name));
            if (cookies) foreach (var name in FirefoxCookies) Remove(Path.Combine(profile, name));
        }
        if (cache)
            foreach (var profile in Folders(Path.Combine(FirefoxCacheRoot, "Profiles")))
                foreach (var name in new[] { "cache2", "startupCache", "thumbnails" }) Remove(Path.Combine(profile, name));
        return new Cleared(files, bytes, failed);
    }

    static IEnumerable<string> Folders(string path) => Directory.Exists(path) ? Directory.EnumerateDirectories(path) : [];

    // ---- profiles -----------------------------------------------------------------------------------

    public List<BrowserRules.Profile> Profiles()
    {
        var result = new List<BrowserRules.Profile>();
        foreach (var (browser, userData) in ChromiumRoots())
        {
            var named = ChromiumNames(userData);
            foreach (var folder in Folders(userData).Select(path => Path.GetFileName(path)!).Where(IsChromiumProfileFolder)
                         .Where(name => !name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(name => name.Equals("Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                named.TryGetValue(folder, out var info);
                result.Add(new BrowserRules.Profile(browser, folder, info.Name ?? folder, info.Account ?? "",
                    folder.Equals("Default", StringComparison.OrdinalIgnoreCase)));
            }
        }
        var firefox = FirefoxProfiles();
        result.AddRange(firefox.Select(profile => new BrowserRules.Profile("Firefox", profile.Path, profile.Name, "", profile.IsFirst)));
        return result;
    }

    /// <summary>The display name and signed-in account of each Chrome or Edge profile folder, from the browser's own list.</summary>
    static Dictionary<string, (string? Name, string? Account)> ChromiumNames(string userData)
    {
        var names = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var state = Path.Combine(userData, "Local State");
            if (!File.Exists(state)) return names;
            if (JsonNode.Parse(File.ReadAllText(state))?["profile"]?["info_cache"] is JsonObject cache)
                foreach (var (folder, info) in cache)
                    names[folder] = (info?["name"]?.GetValue<string>(), info?["user_name"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidOperationException) { }
        return names;
    }

    sealed record FirefoxProfile(string Section, string Name, string Path, bool Relative, bool IsFirst);

    /// <summary>
    /// The profiles in Firefox's profiles.ini. The one to keep is the one Firefox itself starts
    /// with: named by an [Install...] section, or marked Default=1, or else the first listed.
    /// </summary>
    List<FirefoxProfile> FirefoxProfiles()
    {
        var ini = Path.Combine(FirefoxRoot, "profiles.ini");
        if (!File.Exists(ini)) return [];
        var sections = IniSections(File.ReadAllLines(ini));
        var installDefaults = sections.Where(section => section.Name.StartsWith("Install", StringComparison.OrdinalIgnoreCase))
            .Select(section => section.Values.GetValueOrDefault("Default", "")).Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profiles = sections.Where(section => section.Name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) && section.Values.ContainsKey("Path")).ToList();
        string? first = profiles.FirstOrDefault(section => installDefaults.Contains(section.Values["Path"]))?.Name
                        ?? profiles.FirstOrDefault(section => section.Values.GetValueOrDefault("Default") == "1")?.Name
                        ?? profiles.FirstOrDefault()?.Name;
        return profiles.Select(section => new FirefoxProfile(section.Name, section.Values.GetValueOrDefault("Name", section.Values["Path"]),
            section.Values["Path"], section.Values.GetValueOrDefault("IsRelative", "1") == "1", section.Name == first)).ToList();
    }

    sealed record IniSection(string Name, Dictionary<string, string> Values);

    static List<IniSection> IniSections(IEnumerable<string> lines)
    {
        var sections = new List<IniSection>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) sections.Add(new IniSection(line[1..^1], new(StringComparer.OrdinalIgnoreCase)));
            else if (sections.Count > 0 && line.IndexOf('=') is var equals and > 0) sections[^1].Values[line[..equals]] = line[(equals + 1)..];
        }
        return sections;
    }

    /// <summary>
    /// Removes every profile except each browser's first one (or only those named in <paramref name="only"/>, never a first one), with everything in it (bookmarks,
    /// history, saved passwords), and takes it out of the browser's own list. Returns what was removed.
    /// </summary>
    public List<BrowserRules.Profile> RemoveExtraProfiles(IReadOnlySet<string>? only = null)
    {
        var removed = new List<BrowserRules.Profile>();
        // A browser's first profile is never removed, even when named: the browser needs one to start with.
        var extra = Profiles().Where(profile => !profile.IsFirst && (only is null || only.Contains(profile.Key))).ToList();
        foreach (var (browser, userData) in ChromiumRoots())
        {
            var gone = new List<string>();
            foreach (var profile in extra.Where(profile => profile.Browser == browser))
            {
                // Only a folder name the browser itself would create, directly under its own data folder.
                if (!IsChromiumProfileFolder(profile.Id) || profile.Id.Contains('\\') || profile.Id.Contains('/')) continue;
                if (!TryDeleteFolder(Path.Combine(userData, profile.Id))) continue;
                gone.Add(profile.Id);
                removed.Add(profile);
            }
            if (gone.Count > 0) ForgetChromiumProfiles(userData, gone);
        }

        var firefox = FirefoxProfiles().Where(profile => !profile.IsFirst &&
            (only is null || only.Contains(new BrowserRules.Profile("Firefox", profile.Path, "", "", false).Key))).ToList();
        var deleted = new List<string>();
        foreach (var profile in firefox)
        {
            // A profile kept somewhere else on the disk (IsRelative=0) is not ours to delete.
            if (!profile.Relative || profile.Path.Contains("..")) continue;
            var relative = profile.Path.Replace('/', Path.DirectorySeparatorChar);
            if (!TryDeleteFolder(Path.Combine(FirefoxRoot, relative))) continue;
            TryDeleteFolder(Path.Combine(FirefoxCacheRoot, relative));
            deleted.Add(profile.Section);
            removed.Add(new BrowserRules.Profile("Firefox", profile.Path, profile.Name, "", false));
        }
        if (deleted.Count > 0) ForgetFirefoxProfiles(deleted);
        return removed;
    }

    static bool TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    static void ForgetChromiumProfiles(string userData, List<string> folders)
    {
        try
        {
            var path = Path.Combine(userData, "Local State");
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject state) return;
            if (state["profile"] is JsonObject profile)
            {
                if (profile["info_cache"] is JsonObject cache)
                    foreach (var folder in folders) cache.Remove(folder);
                foreach (var listName in new[] { "profiles_order", "last_active_profiles" })
                    if (profile[listName] is JsonArray list)
                        for (int i = list.Count - 1; i >= 0; i--)
                            if (folders.Contains(list[i]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase)) list.RemoveAt(i);
                if (folders.Contains(profile["last_used"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase)) profile["last_used"] = "Default";
            }
            File.WriteAllText(path, state.ToJsonString());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidOperationException or UnauthorizedAccessException) { }
    }

    /// <summary>Rewrites profiles.ini without the removed sections; Firefox wants [Profile0], [Profile1]... without gaps.</summary>
    void ForgetFirefoxProfiles(List<string> sections)
    {
        var ini = Path.Combine(FirefoxRoot, "profiles.ini");
        var output = new List<string>();
        bool skipping = false;
        int next = 0;
        foreach (var raw in File.ReadAllLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1];
                skipping = sections.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (skipping) continue;
                if (name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) && int.TryParse(name["Profile".Length..], out _))
                {
                    output.Add($"[Profile{next++}]");
                    continue;
                }
            }
            if (!skipping) output.Add(raw);
        }
        File.WriteAllLines(ini, output);
    }
}
