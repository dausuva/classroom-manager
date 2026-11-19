// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// Process-lifetime cache for autorun scans. It deliberately is not written to host.json: a Host
/// restart or a power action invalidates observations that may no longer describe the client.
/// </summary>
static class AutorunSnapshotCache
{
    static readonly Dictionary<string, List<AutorunEntry>> Entries = new(StringComparer.OrdinalIgnoreCase);

    static string Key(ClientInfo client, string level) => $"{client.Address}\0{level}";

    public static bool TryGet(ClientInfo client, string level, out List<AutorunEntry> entries)
    {
        if (Entries.TryGetValue(Key(client, level), out var saved))
        {
            entries = saved;
            return true;
        }
        entries = [];
        return false;
    }

    public static void Set(ClientInfo client, string level, List<AutorunEntry> entries) =>
        Entries[Key(client, level)] = entries;

    public static void Invalidate(IEnumerable<ClientInfo> clients)
    {
        var addresses = clients.Select(client => client.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in Entries.Keys.Where(key => addresses.Contains(key.Split('\0')[0])).ToList())
            Entries.Remove(key);
    }
}
