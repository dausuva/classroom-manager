// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>An access key that was removed while this computer could not be reached; the removal is finished when it next answers.</summary>
public sealed class PendingRevocation
{
    public string Name { get; set; } = "";
    /// <summary>The computer's client id when it was known, otherwise "address:" and its address.</summary>
    public string Computer { get; set; } = "";
    public DateTime RequestedUtc { get; set; }
}

/// <summary>
/// The Host's list of removals still to be finished (T-189). A named access key is removed on each Client separately, so a computer that is off
/// keeps the key until it is reached again. The Host remembers which computers still owe a removal and finishes it at the next status check.
/// </summary>
public static class KeyRevocations
{
    static string Id(ClientInfo client) => client.ClientId.Length > 0 ? client.ClientId : "address:" + client.Address;

    static bool Matches(PendingRevocation entry, ClientInfo client) =>
        entry.Computer.Equals(Id(client), StringComparison.OrdinalIgnoreCase) ||
        entry.Computer.Equals("address:" + client.Address, StringComparison.OrdinalIgnoreCase);

    /// <summary>Remembers that these computers still have to remove the key; one entry per key name and computer.</summary>
    public static int Record(HostSettings settings, string name, IEnumerable<ClientInfo> notReached, DateTime nowUtc)
    {
        var added = 0;
        foreach (var client in notReached)
        {
            if (settings.PendingRevocations.Any(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && Matches(entry, client))) continue;
            settings.PendingRevocations.Add(new PendingRevocation { Name = name, Computer = Id(client), RequestedUtc = nowUtc });
            added++;
        }
        return added;
    }

    /// <summary>A key added under this name again replaces the revoked one, so the waiting removals are cancelled.</summary>
    public static int Cancel(HostSettings settings, string name) =>
        settings.PendingRevocations.RemoveAll(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> For(HostSettings settings, ClientInfo client) =>
        settings.PendingRevocations.Where(entry => Matches(entry, client)).Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Sends the waiting removals to a computer that answered. A computer that no longer has a key by that name counts as done.
    /// Returns the names that were removed or were already gone, and the ones that could not be removed (still waiting).
    /// </summary>
    public static async Task<(IReadOnlyList<string> Done, IReadOnlyList<string> Failed)> ApplyAsync(
        HostSettings settings, ClientInfo client, ClientSession session, CancellationToken ct = default)
    {
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var name in For(settings, client))
        {
            try
            {
                await session.CallAsync(Cmd.AccessKeysRemove, new JsonObject { ["name"] = name }, ct);
                done.Add(name);
            }
            catch (RemoteException ex) when (ex.Message.StartsWith("This computer has no access key named", StringComparison.Ordinal))
            {
                done.Add(name);
            }
            catch (Exception ex) when (ex is RemoteException or IOException or TimeoutException)
            {
                failed.Add(name);
            }
        }
        foreach (var name in done)
            settings.PendingRevocations.RemoveAll(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && Matches(entry, client));
        return (done, failed);
    }

    /// <summary>How many computers still owe a removal, for the activity line and the security report.</summary>
    public static int Waiting(HostSettings settings) => settings.PendingRevocations.Count;
}
