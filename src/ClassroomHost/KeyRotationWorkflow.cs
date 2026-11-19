// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// Rotating the default lab key (MW-G): which computers may be changed, the change of this PC's settings that makes a half-finished rotation recoverable, how
/// many computers are not on the new key yet, and when the old keys can be forgotten. The sending and the proof on a fresh connection stay in
/// <see cref="KeyRotation"/>; the dialogs stay in the window. This is trusted core, not a module or plugin surface, and it never learns a key's value beyond
/// what the settings already hold.
/// </summary>
public sealed class KeyRotationWorkflow
{
    readonly HostSettings _settings;

    public KeyRotationWorkflow(HostSettings settings) => _settings = settings;

    /// <summary>
    /// The computers among <paramref name="targets"/> that do not use the default lab key (an earlier key of it counts). Rotating changes only the default
    /// key, so any computer on another lab key, or one whose key is not known yet, must be left out before anything is changed.
    /// </summary>
    public List<ClientInfo> IncompatibleTargets(IEnumerable<ClientInfo> targets)
    {
        var defaultFingerprints = _settings.PreviousPsks.Append(_settings.Psk)
            .Where(key => !string.IsNullOrWhiteSpace(key)).Select(SharedKey.Fingerprint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return targets.Where(client => client.KeyId.Length == 0 || !defaultFingerprints.Contains(client.KeyId)).ToList();
    }

    /// <summary>
    /// Makes the new key this PC's current key and keeps the old one as accepted, saved at once: if the app is closed half-way it still knows both keys and
    /// can finish later. A backup of the settings is taken first; <paramref name="backupFailed"/> hears why it could not be, and the change goes on.
    /// </summary>
    public void Begin(string newKey, bool autoFinish, Action<string> backupFailed)
    {
        try { HostBackups.Snapshot(HostSettings.PathOnDisk, HostBackups.BeforeKeyChange); }
        catch (System.IO.IOException ex) { backupFailed("Could not save a backup before the key change: " + ex.Message); }

        var oldKey = _settings.Psk;
        _settings.PreviousPsks.Remove(oldKey);
        _settings.PreviousPsks.Remove(newKey);
        _settings.PreviousPsks.Insert(0, oldKey);
        _settings.Psk = newKey;
        _settings.AutoFinishKeyRotation = autoFinish;
        _settings.StartNewKeyEpoch(DateTime.UtcNow);
        _settings.Save();
    }

    /// <summary>How many of the computers using the default lab key (old or new) are not confirmed on <paramref name="newKey"/> yet.</summary>
    public int NotOnNewKey(IEnumerable<ClientInfo> clients, string newKey)
    {
        var newId = SharedKey.Fingerprint(newKey);
        var defaultKeyIds = _settings.PreviousPsks.Append(_settings.Psk).Select(SharedKey.Fingerprint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return clients.Count(client => defaultKeyIds.Contains(client.KeyId) && client.KeyId != newId);
    }

    /// <summary>
    /// Stops accepting keys from before a rotation once every computer in the list that used any of them has confirmed the current key. Returns true when
    /// the old keys were forgotten.
    /// </summary>
    public bool ForgetOldKeysIfDone(IReadOnlyCollection<ClientInfo> clients)
    {
        if (_settings.PreviousPsks.Count == 0 || clients.Count == 0) return false;
        var current = SharedKey.Fingerprint(_settings.Psk);
        var relatedIds = _settings.PreviousPsks.Append(_settings.Psk).Select(SharedKey.Fingerprint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var related = clients.Where(client => relatedIds.Contains(client.KeyId)).ToList();
        if (related.Count == 0 || related.Any(client => client.KeyId != current)) return false;
        _settings.PreviousPsks.Clear();
        _settings.Save();
        return true;
    }

    /// <summary>
    /// Sends <paramref name="newKey"/> over an open session, then proves it with a fresh connection that uses only the new key. Recovery also uses a fresh
    /// current-key connection, never the revoked old session. Returns true when the computer already had the key.
    /// </summary>
    public async Task<bool> PushAsync(ClientInfo client, ClientSession session, string newKey)
    {
        if (session.KeyUsed == newKey)
        {
            client.KeyId = SharedKey.Fingerprint(newKey);
            client.KeyName = _settings.NormalizedDefaultKeyName();
            return true;
        }

        await KeyRotation.ApplyAndVerifyAsync(client, _settings, session, newKey, _settings.KeyEpoch);
        client.KeyId = SharedKey.Fingerprint(newKey);
        client.KeyName = _settings.NormalizedDefaultKeyName();
        return false;
    }
}
