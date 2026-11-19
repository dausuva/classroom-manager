// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Rotate, verify and (if necessary) restore without reusing a revoked connection.</summary>
public static class KeyRotation
{
    public static async Task ApplyAndVerifyAsync(ClientInfo client, HostSettings settings, ClientSession previous, string newKey, int? epoch = null)
    {
        // The epoch the computer is on now, so that a failed rotation puts back the epoch with the key (a Client without the field answers none).
        int? priorEpoch = null;
        if (epoch is not null)
        {
            try { priorEpoch = (await previous.CallAsync(Cmd.Ping))["keyEpoch"]?.GetValue<int>(); }
            catch (Exception ex) when (ex is RemoteException or IOException or TimeoutException) { }
        }
        var request = new JsonObject { ["key"] = newKey };
        if (epoch is { } counted) request["epoch"] = counted;
        try { await previous.CallAsync(Cmd.KeySet, request); }
        catch (RemoteException ex) when (ex.Message.StartsWith("Unknown command"))
        {
            throw new InvalidOperationException("this client version can't change its key remotely - use Update client first, then rotate again");
        }

        try { await VerifyAsync(client, settings, newKey); }
        catch (Exception ex)
        {
            try
            {
                // The original connection is revoked now. Recovery needs a current credential,
                // never a privileged exception allowing the old session to keep issuing commands.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var recovery = await ClientSession.OpenWithKeyAsync(client, settings, newKey, deadline.Token);
                var restore = new JsonObject { ["key"] = previous.KeyUsed };
                if (priorEpoch is { } before) restore["epoch"] = before;
                await recovery.CallAsync(Cmd.KeySet, restore, deadline.Token);
                await VerifyAsync(client, settings, previous.KeyUsed);
            }
            catch
            {
                throw new InvalidOperationException($"the new key could not be verified ({ex.Message}) and the previous key could not be put back; use Refresh status to see which key it accepts");
            }
            throw new InvalidOperationException($"the new key could not be verified ({ex.Message}); the computer keeps its previous key");
        }
    }

    static async Task VerifyAsync(ClientInfo client, HostSettings settings, string key)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var check = await ClientSession.OpenWithKeyAsync(client, settings, key, deadline.Token);
        await check.CallAsync(Cmd.Ping, ct: deadline.Token);
    }
}
