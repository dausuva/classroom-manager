// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>What the Host window does first when it opens, decided without a window (T-253 phase 3): the entry the saved computers become, the exam scope an older Host did not record, and which first step a Host with nothing in it is led through.</summary>
public static class StartupRules
{
    /// <summary>The list entry for a computer remembered in the settings: not yet checked (or "discovered" when it has no room), with the key it last used named.</summary>
    public static ClientInfo ClientFor(SavedComputer saved, HostSettings settings) => new()
    {
        Address = saved.Address, ComputerName = saved.Name, Alias = saved.Alias, ClientId = saved.ClientId,
        Mac = ComputerIdentity.NormalizeMac(saved.Mac), Port = saved.Port, Room = saved.Room, KeyId = saved.KeyId,
        KeyName = settings.KeyNameFromFingerprint(saved.KeyId),
        Status = saved.Room.Length > 0 ? "Not checked" : "Discovered - assign to a room to monitor",
    };

    /// <summary>
    /// Settings saved by a build that did not record which computers an exam covers: it covered every assigned one. Returns true when the settings were completed this way.
    /// </summary>
    public static bool CoverAssignedWhenUnrecorded(HostSettings settings, IEnumerable<ClientInfo> clients)
    {
        if (!settings.ExamModeEnabled || settings.ExamModeComputers.Count != 0) return false;
        foreach (var client in clients.Where(client => client.Room.Length > 0))
            ExamModeScope.Add(settings.ExamModeComputers, client.ClientId, client.Address);
        return true;
    }

    public enum FirstStep { None, SetupAssistant, SettingsDialog }

    /// <summary>
    /// A Host with no key and no computers is led through the first steps instead of opening on an empty list; one that has computers but no key is taken to the settings
    /// to enter it; any other opens as it is.
    /// </summary>
    public static FirstStep FirstStepFor(bool hasHostKeys, int computersListed) =>
        hasHostKeys ? FirstStep.None : computersListed == 0 ? FirstStep.SetupAssistant : FirstStep.SettingsDialog;
}
