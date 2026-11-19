// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// Finds saved computers again after their address changed. The Host connects to the address it
/// saved; when the network hands a computer another one (DHCP), that computer shows as Offline
/// although it is on and answers discovery. Scan network corrects this, but only when someone
/// presses it, so the background refresh also looks for computers that stopped answering.
/// Only a computer's own identity (network card address, or client id) moves an entry; a name never does.
/// </summary>
public static class AddressFollower
{
    /// <summary>How often the background refresh may send a discovery broadcast for missing computers.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>A room computer that does not answer at its saved address and can be recognised elsewhere.</summary>
    public static bool IsMissing(ClientInfo computer) =>
        computer.Unreachable && computer.Room.Length > 0 && computer.BusyCount == 0 &&
        computer.ExclusiveOperation.Length == 0 && ComputerIdentity.HasStableIdentity(computer.ClientId, computer.Mac);

    /// <summary>
    /// A computer answered at a saved address, but it is another computer: both network cards are known and differ,
    /// unless both sides also give the same client id (a replaced network card, or an image copied with its
    /// client.json, is still the same saved computer). Such an entry must not take over the other's identity.
    /// </summary>
    public static bool AnswersAsAnother(ClientInfo saved, string? replyClientId, string? replyMac)
    {
        var savedCard = ComputerIdentity.NormalizeMac(saved.Mac);
        var replyCard = ComputerIdentity.NormalizeMac(replyMac);
        if (savedCard.Length == 0 || replyCard.Length == 0 || savedCard == replyCard) return false;
        var savedId = saved.ClientId.Trim();
        var replyId = (replyClientId ?? "").Trim();
        return !(savedId.Length > 0 && savedId.Equals(replyId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The discovery answers that come from a missing computer at an address other than the saved one.</summary>
    public static List<(ClientInfo Computer, Discovery.Found Found)> Moved(
        IEnumerable<ClientInfo> missing, IReadOnlyCollection<Discovery.Found> found)
    {
        var moved = new List<(ClientInfo, Discovery.Found)>();
        foreach (var computer in missing)
        {
            var answers = found.Where(answer =>
                ComputerIdentity.StableMatch(computer.ClientId, computer.Mac, answer.Info.ClientId, answer.Info.Mac)).ToList();
            // Two answers with one identity (a cloned client.json without network card addresses) say nothing certain.
            if (answers.Count == 1 && !answers[0].Address.ToString().Equals(computer.Address, StringComparison.OrdinalIgnoreCase))
                moved.Add((computer, answers[0]));
        }
        return moved;
    }
}
