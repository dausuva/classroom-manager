// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>Process-lifetime cache for batch Scheduled Tasks inventory scans.</summary>
static class TaskSnapshotCache
{
    static readonly Dictionary<string, List<TaskEntry>> Entries = new(StringComparer.OrdinalIgnoreCase);

    static string Key(ClientInfo client) => client.ClientId.Length > 0
        ? "id:" + client.ClientId
        : client.Mac.Length > 0 ? "mac:" + client.Mac : "address:" + client.Address;

    public static bool TryGet(ClientInfo client, out List<TaskEntry> entries)
    {
        if (Entries.TryGetValue(Key(client), out var saved))
        {
            entries = saved;
            return true;
        }
        entries = [];
        return false;
    }

    public static void Set(ClientInfo client, List<TaskEntry> entries) => Entries[Key(client)] = entries;

    public static void Invalidate(IEnumerable<ClientInfo> clients)
    {
        foreach (var client in clients) Entries.Remove(Key(client));
    }
}
