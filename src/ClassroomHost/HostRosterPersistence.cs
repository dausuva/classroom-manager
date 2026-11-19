// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Owns the saved snapshot of a live roster and delegates file persistence to the settings store.</summary>
public sealed class HostRosterPersistence
{
    readonly HostSettings _settings;
    readonly Action<HostSettings> _save;

    public HostRosterPersistence(HostSettings settings, Action<HostSettings>? save = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _save = save ?? (value => value.Save());
    }

    /// <summary>
    /// Materializes detached records before changing settings. A failed save restores the previous
    /// in-memory roster; callers retain the live edits for retry. File recovery belongs to the store.
    /// Call synchronously on the roster's owning thread.
    /// </summary>
    public void Save(IEnumerable<ClientInfo> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        if (HostSettings.SavesSuspended) return;
        var snapshot = clients.Select(client => new SavedComputer
        {
            Address = client.Address, Name = client.ComputerName, Alias = client.Alias, ClientId = client.ClientId,
            Mac = ComputerIdentity.NormalizeMac(client.Mac), Port = client.Port, Room = client.Room, KeyId = client.KeyId,
        }).ToList();

        var previous = _settings.Computers;
        _settings.Computers = snapshot;
        try { _save(_settings); }
        catch (HostSettingsCommitException ex) when (ex.Outcome == HostSettingsCommitOutcome.Committed)
        {
            // The primary settings/keys are durable even if a later backup step failed.
            throw;
        }
        catch
        {
            _settings.Computers = previous;
            throw;
        }
    }
}
