// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>Core roster admission around the existing stable-identity matcher; discovery never authorizes a certificate.</summary>
public static class AddressFollowTargets
{
    public static bool IsMissing(StatusRefreshTarget target, IReadOnlyCollection<ClientInfo> roster, Func<ClientInfo, bool> selected) =>
        target.Matches() && roster.Contains(target.Client) && selected(target.Client) && AddressFollower.IsMissing(target.Client);

    public static IReadOnlyList<(StatusRefreshTarget Target, Discovery.Found Answer)> Plan(
        IReadOnlyList<StatusRefreshTarget> captured, IReadOnlyCollection<ClientInfo> roster,
        Func<ClientInfo, bool> selected, IReadOnlyCollection<Discovery.Found> answers)
    {
        // Automatic following must not let roster merging choose a different (possibly busy) saved row.
        // Leave duplicate saved identities to the explicit manual-scan reconciliation path.
        var eligible = captured.Where(target => IsMissing(target, roster, selected) && roster.Count(client =>
            ComputerIdentity.StableMatch(target.ClientId, target.Mac, client.ClientId, client.Mac)) == 1).ToArray();
        return AddressFollower.Moved(eligible.Select(target => target.Client), answers)
            .Select(move => (eligible.First(target => ReferenceEquals(target.Client, move.Computer)), move.Found)).ToArray();
    }
}
