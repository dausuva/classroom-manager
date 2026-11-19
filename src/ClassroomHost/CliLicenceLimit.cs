// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// The free tier (T-220) for the command line and the scheduled runs: an official Host without a licence acts on at most three computers online at once. The first
/// computers that answer, in list order, are in use; the others get a result that says they wait for a licence and what to do about it.
/// </summary>
public static class CliLicenceLimit
{
    /// <summary>Removes from <paramref name="clients"/> the computers that must wait, and returns a failed result for each of them.</summary>
    public static Task<List<CliResult>> ApplyAsync(List<ClientInfo> clients, string command, CancellationToken ct) =>
        ApplyAsync(clients, command, LicenceCapacity.AllowedNow(), client => LicenceCapacity.IsOnlineAsync(client.Address, client.Port, TimeSpan.FromSeconds(2), ct));

    /// <summary>The same with the limit and the way of learning who is online given, so a test can stand in for the network.</summary>
    internal static async Task<List<CliResult>> ApplyAsync(List<ClientInfo> clients, string command, int? allowed, Func<ClientInfo, Task<bool>> isOnline)
    {
        var waitingResults = new List<CliResult>();
        if (allowed is not { } limit || LicenceCapacity.CommandsNotLimited.Contains(command)) return waitingResults;
        var answers = await Task.WhenAll(clients.Select(isOnline));
        for (var i = 0; i < clients.Count; i++) if (answers[i]) clients[i].Status = "Online";
        var plan = LicenceCapacity.Plan(clients, clients, limit);
        foreach (var waiting in plan.Waiting)
            waitingResults.Add(new CliResult(waiting.Name, waiting.Address, waiting.Room, false,
                $"Waiting for a licence: Classroom Manager is free for up to {limit} computers online at the same time. Add a licence file in Help > About in the Host."));
        clients.Clear();
        clients.AddRange(plan.Acting);
        return waitingResults;
    }
}
