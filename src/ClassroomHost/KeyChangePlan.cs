// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// What a lab key change will do, said before it is done (T-169, T-189): which computers answer now, which are off and will take the key when
/// they are next seen, how long they have, and what must be done by hand afterwards. Pure, so it can be tested without a window.
/// </summary>
public sealed record KeyChangePlan(IReadOnlyList<string> Online, IReadOnlyList<string> Offline, int GraceDays, string KeyName)
{
    public static KeyChangePlan Build(IEnumerable<ClientInfo> targets, int graceDays, string keyName)
    {
        var list = targets.ToList();
        bool IsOn(ClientInfo client) => client.Status.StartsWith("Online", StringComparison.OrdinalIgnoreCase);
        return new KeyChangePlan(
            list.Where(IsOn).Select(client => client.Name).ToList(),
            list.Where(client => !IsOn(client)).Select(client => client.Name).ToList(),
            graceDays, keyName);
    }

    /// <summary>The confirmation, one sentence per line (each is translated on its own): what changes now, what waits, what must be done by hand.</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string> { $"{Online.Count} computer(s) answer now and get the new key at once." };
        if (Offline.Count > 0)
            lines.Add($"{Offline.Count} computer(s) are off or not answering ({Names(Offline)}). They keep the old key until this app sees them again, then get the new one; after {GraceDays} days a computer that still has the old key is reported as a problem.");
        lines.Add("A copy of the settings and keys is saved first, so this can be undone from the Host's backups.");
        lines.Add("Afterwards, any other teacher PC and any USB setup kit that still has the old key must be given the new one.");
        return lines;
    }

    public string Describe() => string.Join("\n", Lines());

    /// <summary>The words the person must type to go on: the name of the lab key, so that a key change is never a reflex click.</summary>
    public bool Confirms(string typed) => typed.Trim().Equals(KeyName.Trim(), StringComparison.OrdinalIgnoreCase);

    static string Names(IReadOnlyList<string> names) => string.Join(", ", names.Take(8)) + (names.Count > 8 ? ", ..." : "");
}
