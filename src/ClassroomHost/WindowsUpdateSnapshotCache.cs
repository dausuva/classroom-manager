// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassroomManager.Host;

/// <summary>
/// The last Windows Update scan of each computer, kept in the teacher's data folder so PC information and the
/// Windows Update window do not each ask the computer again. A saved list always carries the time it was read;
/// the windows show that age and never present it as current.
/// </summary>
public static class WindowsUpdateSnapshotCache
{
    public sealed record Snapshot(DateTime At, bool IncludedOptional, JsonArray Updates);

    /// <summary>A scan older than this is not worth showing instead of asking the computer.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    static string FilePath => Path.Combine(HostSettings.DataFolder, "windows-update-cache.json");

    static string Key(ClientInfo client) => client.ClientId.Length > 0
        ? "id:" + client.ClientId
        : client.Mac.Length > 0 ? "mac:" + client.Mac : "address:" + client.Address;

    public static void Set(ClientInfo client, bool includedOptional, JsonArray updates, DateTime? at = null)
    {
        var all = ReadAll();
        all[Key(client)] = new JsonObject
        {
            ["at"] = (at ?? DateTime.Now).ToString("O"),
            ["optional"] = includedOptional,
            ["updates"] = JsonNode.Parse(updates.ToJsonString()),
        };
        try
        {
            Directory.CreateDirectory(HostSettings.DataFolder);
            File.WriteAllText(FilePath, all.ToJsonString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static bool TryGet(ClientInfo client, out Snapshot snapshot, DateTime? now = null)
    {
        snapshot = new Snapshot(DateTime.MinValue, false, []);
        if (ReadAll()[Key(client)] is not JsonObject item) return false;
        if (!DateTime.TryParse(item["at"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)) return false;
        if ((now ?? DateTime.Now) - at > MaxAge || at > (now ?? DateTime.Now).AddMinutes(5)) return false;
        if (item["updates"] is not JsonArray updates) return false;
        snapshot = new Snapshot(at, item["optional"]?.GetValue<bool>() == true, updates);
        return true;
    }

    /// <summary>What the Windows Update window says about a saved list: how old it is, in one translatable sentence.</summary>
    public static string SavedText(DateTime at, DateTime now, int count)
    {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1)) return $"List read less than a minute ago ({count} update(s)). Choose Check for updates to ask the computer again.";
        if (age < TimeSpan.FromHours(1)) return $"List read {(int)age.TotalMinutes} minute(s) ago ({count} update(s)). Choose Check for updates to ask the computer again.";
        return $"List read {(int)age.TotalHours} hour(s) ago ({count} update(s)). Choose Check for updates to ask the computer again.";
    }

    static JsonObject ReadAll()
    {
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject saved) return saved;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new JsonObject();
    }
}
