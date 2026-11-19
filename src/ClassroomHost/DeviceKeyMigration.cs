// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// Gives one computer its own key (T-188): the lab key it was reached with is turned into the key derived for that computer and the
/// computer is told to use it, with the verify-then-commit sequence of an ordinary key change (<see cref="KeyRotation"/>): the new key is
/// tried before the old one is let go, and the old one is put back if the new one does not work. Nothing here runs by itself.
/// </summary>
public static class DeviceKeyMigration
{
    /// <summary>Returns what happened in one line, or throws with the reason when the computer must be left as it is.</summary>
    public static async Task<string> ApplyAsync(ClientInfo client, HostSettings settings, ClientSession session, CancellationToken ct = default)
    {
        var info = await session.CallAsync(Cmd.Ping, ct: ct);
        var clientId = info["clientId"]?.GetValue<string>() ?? "";
        var keyName = info["keyName"]?.GetValue<string>() ?? "";
        if (clientId.Length == 0)
            throw new InvalidOperationException("the computer did not report its client id; update its Client first");
        if (!string.Equals(keyName, AccessKeyNames.Primary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"it was reached with the key '{keyName}', not the lab's original key; only the original key can be replaced");
        if (session.UsesPreviousKey)
            throw new InvalidOperationException("it still accepts only a key from before the last key change; finish that change first");
        if (DeviceKeys.IsDerived(session.KeyUsed))
        {
            // Nothing to change on the computer, but the Host may not have remembered it (an earlier run did not save): do it now, so running the command again repairs that.
            client.ClientId = clientId;
            client.KeyId = SharedKey.Fingerprint(session.KeyUsed);
            return "already has its own key";
        }

        // The key the computer was reached with must be one of this Host's lab keys, or the Host could not derive it again later.
        if (!settings.CurrentHostKeys().Any(key => key.Psk == session.KeyUsed))
            throw new InvalidOperationException("the key it accepted is not one of this Host's lab keys");

        var derived = DeviceKeys.Derive(session.KeyUsed, clientId);
        client.ClientId = clientId;
        await KeyRotation.ApplyAndVerifyAsync(client, settings, session, derived);
        // Remembered, so that the next connection starts with this key instead of the lab key the computer now refuses.
        client.KeyId = SharedKey.Fingerprint(derived);
        client.KeyName = settings.NormalizedDefaultKeyName();
        return $"now has its own key (fingerprint {SharedKey.Fingerprint(derived)})";
    }

    /// <summary>
    /// Writes what the migration learned into the saved computers: the client id and the key id. Without it a Host starts every connection with the
    /// lab key, which the computer refuses; each refusal counts against this PC and enough of them make the computer ignore it for minutes.
    /// </summary>
    public static int Record(HostSettings settings, IEnumerable<ClientInfo> migrated)
    {
        var changed = 0;
        foreach (var client in migrated)
        {
            var saved = settings.Computers.FirstOrDefault(computer =>
                computer.Address.Equals(client.Address, StringComparison.OrdinalIgnoreCase) && (computer.Port == client.Port || client.Port == 0));
            if (saved is null || client.KeyId.Length == 0) continue;
            saved.KeyId = client.KeyId;
            if (client.ClientId.Length > 0) saved.ClientId = client.ClientId;
            changed++;
        }
        return changed;
    }
}
