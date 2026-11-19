// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary><c>ClassroomCli security</c>: asks every saved computer, builds the lab report and prints it. Changes nothing.</summary>
public static class SecurityCli
{
    public static async Task<int> RunAsync(HostSettings settings, CliOptions options, TextWriter output)
    {
        var clients = settings.Computers.Select(computer => new ClientInfo
        {
            Address = computer.Address, Name = computer.Name.Length > 0 ? computer.Name : computer.Address, ClientId = computer.ClientId,
            Mac = ComputerIdentity.NormalizeMac(computer.Mac), Port = computer.Port, Room = computer.Room, KeyId = computer.KeyId,
        }).ToList();
        var computers = await GatherAsync(settings, clients, options.Parallel, CancellationToken.None);
        var rows = LabSecurityReport.Build(settings, computers);
        if (options.Json)
            output.WriteLine(JsonSerializer.Serialize(new
            {
                overall = LabSecurityReport.Overall(rows).ToString(),
                rows = rows.Select(row => new { state = row.State.ToString(), title = row.Title, detail = row.Detail, action = row.Action }),
            }, new JsonSerializerOptions { WriteIndented = true }));
        else
            output.Write(LabSecurityReport.Text(rows));
        return LabSecurityReport.Overall(rows) >= LabState.Warning ? HostCli.ExitFailures : HostCli.ExitOk;
    }

    /// <summary>Asks every computer once (Ping) and returns what each said; one that does not answer is listed as not online.</summary>
    public static async Task<List<ComputerSecurity>> GatherAsync(HostSettings settings, IReadOnlyList<ClientInfo> clients, int parallel, CancellationToken ct)
    {
        var answers = new ConcurrentDictionary<ClientInfo, ComputerSecurity>();
        await BulkRunner.RunAsync(clients, settings, async (client, session) =>
        {
            var info = await session.CallAsync(Cmd.Ping, ct: ct);
            answers[client] = FromPing(client.Name, info, session.KeyUsed);
        }, parallel, ct, new BulkRunCallbacks());
        return clients.Select(client => answers.TryGetValue(client, out var known)
            ? known
            : new ComputerSecurity(client.Name, client.ClientId, false, "", client.KeyId)).ToList();
    }

    /// <summary>What one answer to Ping says about the computer's security.</summary>
    public static ComputerSecurity FromPing(string name, JsonObject info, string keyUsed)
    {
        var counts = info["security"]?["counts"] as JsonObject;
        long Count(string kind) => counts?[kind] is JsonNode node ? node.GetValue<long>() : 0;
        var hardening = info["hardening"] as JsonObject;
        return new ComputerSecurity(
            name, info["clientId"]?.GetValue<string>() ?? "", true, info["version"]?.GetValue<string>() ?? "", SharedKey.Fingerprint(keyUsed),
            hardening?["legacyDiscovery"]?.GetValue<bool>(), hardening?["requireChannelBinding"]?.GetValue<bool>(),
            Count("handshake-failed"), Count("log-chain-broken"),
            DateTime.TryParse(info["keyChangedUtc"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var changed) ? changed : null,
            info["keyEpoch"]?.GetValue<int>(),
            DateTime.TryParse(info["certificateNotAfterUtc"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var certificateEnds) ? certificateEnds : null);
    }
}
