// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public enum LabState { Ok, Info, Warning, Problem }

/// <summary>What the Host knows about one computer's security, taken from its last answer.</summary>
public sealed record ComputerSecurity(
    string Name, string ClientId, bool Online, string Protocol, string KeyId,
    bool? LegacyDiscovery = null, bool? RequiresBinding = null, long FailedHandshakes = 0, long BrokenChains = 0, DateTime? KeyChangedUtc = null, int? KeyEpoch = null, DateTime? CertificateNotAfterUtc = null);

/// <summary>One line of the report: a property of the lab, whether it holds, what it means and what to do about it.</summary>
public sealed record LabSecurityRow(LabState State, string Title, string Detail, string Action = "");

/// <summary>
/// "Secure this lab" (T-194): every property of the security plan that the Host can see, as a row with a state and, when something is
/// not as it should be, the command that fixes it. A pure function of the settings and of what the computers last reported, so it can be
/// shown in a window, printed by <c>ClassroomCli security</c> and tested with a deliberately weakened fake lab.
/// </summary>
public static class LabSecurityReport
{
    public static IReadOnlyList<LabSecurityRow> Build(HostSettings settings, IReadOnlyList<ComputerSecurity> computers)
    {
        var rows = new List<LabSecurityRow>();
        var online = computers.Where(computer => computer.Online).ToList();

        // 1. Transport.
        rows.Add(settings.TlsWasOff
            ? new(LabState.Info, "Connection security", "TLS is always on. A settings file of this Host said it was off; that line is ignored.",
                "Nothing to do; the next save removes it.")
            : new(LabState.Ok, "Connection security", "Every connection uses TLS 1.2 or 1.3 and the key proof is checked inside it. There is no setting that turns this off."));

        // 2. Approved certificates for the saved computers.
        var unapproved = settings.Computers.Where(computer => !settings.HasTrustEntry(computer.Address, computer.Port > 0 ? computer.Port : settings.TcpPort, computer.Mac)).ToList();
        rows.Add(settings.Computers.Count == 0
            ? new(LabState.Info, "Approved certificates", "No computers are saved yet.")
            : unapproved.Count == 0
                ? new(LabState.Ok, "Approved certificates", $"All {settings.Computers.Count} saved computers have an approved certificate, so a computer posing as one of them is refused.")
                : new(LabState.Problem, "Approved certificates",
                    $"{unapproved.Count} of {settings.Computers.Count} saved computers have no approved certificate and this Host will not connect to them: {Names(unapproved.Select(computer => computer.Name.Length > 0 ? computer.Name : computer.Address))}.",
                    "ClassroomCli trust-check --live   (then --from-files <folder> with enrolment files from Client Health)"));

        // 3. A key per computer.
        var masterFingerprints = settings.CurrentHostKeys().Select(key => SharedKey.Fingerprint(key.Psk)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var onLabKey = online.Where(computer => masterFingerprints.Contains(computer.KeyId)).ToList();
        var onOwnKey = online.Where(computer => computer.ClientId.Length > 0 && settings.CurrentHostKeys()
            .Any(key => SharedKey.Fingerprint(DeviceKeys.Derive(key.Psk, computer.ClientId)).Equals(computer.KeyId, StringComparison.OrdinalIgnoreCase))).ToList();
        rows.Add(online.Count == 0
            ? new(LabState.Info, "A key per computer", "No computer answered, so this was not checked.")
            : onLabKey.Count == 0 && onOwnKey.Count == online.Count
                ? new(LabState.Ok, "A key per computer", $"All {online.Count} computers that answered hold their own key; a stolen client.json opens only that computer.")
                : onLabKey.Count > 0
                    ? new(LabState.Warning, "A key per computer",
                        $"{onLabKey.Count} of {online.Count} computers still hold the lab key itself: a copied client.json from one of them opens all of them ({Names(onLabKey.Select(computer => computer.Name))}).",
                        "ClassroomCli derive-keys --computer <name> --yes   (one test computer first)")
                    : new(LabState.Info, "A key per computer", "The key these computers accept is not one this Host can tell apart; refresh their status."));

        // 3b. How old the lab key is on each computer (NIST SP 800-57 suggests a cryptoperiod of about a year for a key like this).
        var dated = online.Where(computer => computer.KeyChangedUtc is not null).ToList();
        var stale = dated.Where(computer => (DateTime.UtcNow - computer.KeyChangedUtc!.Value).TotalDays > 365).ToList();
        rows.Add(online.Count == 0
            ? new(LabState.Info, "Key age", "No computer answered, so this was not checked.")
            : dated.Count == 0
                ? new(LabState.Info, "Key age", "No computer has recorded when its key was last changed; it is recorded from the next key change on.",
                    "Settings > Rotate shared key, at a quiet time, when all computers are on.")
                : stale.Count > 0
                    ? new(LabState.Warning, "Key age", $"{stale.Count} of {dated.Count} computers have had the same key for more than a year ({Names(stale.Select(computer => computer.Name))}).",
                        "Settings > Rotate shared key, at a quiet time, when all computers are on.")
                    : new(LabState.Ok, "Key age", $"The oldest key among {dated.Count} computers was set {Math.Round((DateTime.UtcNow - dated.Min(computer => computer.KeyChangedUtc!.Value)).TotalDays)} days ago."
                        + (dated.Count < online.Count ? $" {online.Count - dated.Count} computers have not recorded a date yet." : "")));

        // 3c. Which rotation of the lab key each computer is on, and the grace window for the ones that were off when it happened (T-189).
        var epochKnown = online.Where(computer => computer.KeyEpoch is not null).ToList();
        var behind = epochKnown.Where(computer => computer.KeyEpoch < settings.KeyEpoch).ToList();
        var absent = computers.Where(computer => !computer.Online).ToList();
        var graceEnds = settings.KeyEpochChangedUtc?.AddDays(settings.KeyGraceDays);
        if (settings.KeyEpoch == 0)
            rows.Add(new(LabState.Ok, "Key rotation", "The lab key has not been rotated from this Host yet, so there is nothing to be behind on."));
        else if (behind.Count == 0)
            rows.Add(new(epochKnown.Count == 0 ? LabState.Info : LabState.Ok, "Key rotation",
                epochKnown.Count == 0
                    ? $"Rotation {settings.KeyEpoch} was made, but no computer that answered reports its rotation yet; update their Clients."
                    : $"All {epochKnown.Count} computers that answered are on rotation {settings.KeyEpoch}." + (absent.Count > 0 ? $" {absent.Count} did not answer and were not checked." : "")));
        else if (graceEnds is { } end && DateTime.UtcNow > end)
            rows.Add(new(LabState.Problem, "Key rotation",
                $"{behind.Count} computers are still on an older key, {settings.KeyGraceDays} days after rotation {settings.KeyEpoch} ({Names(behind.Select(computer => computer.Name))}): the grace window is over, so a copy of the old key still opens them.",
                "Run Rotate key again for them, or enrol them again by hand if they cannot be reached."));
        else
            rows.Add(new(LabState.Warning, "Key rotation",
                graceEnds is { } due
                    ? $"{behind.Count} computers have not taken rotation {settings.KeyEpoch} yet ({Names(behind.Select(computer => computer.Name))}); they are switched the next time this Host sees them, until {due:yyyy-MM-dd}."
                    : $"{behind.Count} computers have not taken rotation {settings.KeyEpoch} yet ({Names(behind.Select(computer => computer.Name))}); they are switched the next time this Host sees them.",
                "Switch them on, or run Rotate key again for them."));

        // 3c2. Access keys that were removed while a computer was off and are still on it until the Host sees it again (T-189).
        if (settings.PendingRevocations.Count > 0)
        {
            var oldest = settings.PendingRevocations.Min(entry => entry.RequestedUtc);
            var keyNames = settings.PendingRevocations.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var overdue = DateTime.UtcNow > oldest.AddDays(settings.KeyGraceDays);
            rows.Add(new(overdue ? LabState.Problem : LabState.Warning, "Removed keys",
                $"{settings.PendingRevocations.Count} removals of access keys are still waiting for a computer that was off ({Names(keyNames)}); until the Host sees it, the removed key still opens it.",
                "Switch the computers on with the Host open; if one cannot be reached, remove the key on it by hand."));
        }

        // 3d. How long each computer's TLS certificate still lasts. The Host pins the certificate, so a certificate that ends is a computer the Host stops reaching
        // the day somebody starts checking the dates; a certificate that lasts twenty years is a stolen private key that is useful for twenty years (T-189).
        var certified = online.Where(computer => computer.CertificateNotAfterUtc is not null).ToList();
        var ending = certified.Where(computer => (computer.CertificateNotAfterUtc!.Value - DateTime.UtcNow).TotalDays < 90).ToList();
        var longLived = certified.Where(computer => (computer.CertificateNotAfterUtc!.Value - DateTime.UtcNow).TotalDays > 3650).ToList();
        rows.Add(certified.Count == 0
            ? new(LabState.Info, "Certificate life", online.Count == 0 ? "No computer answered, so this was not checked." : "No computer reports when its certificate ends; update the Clients.")
            : ending.Count > 0
                ? new(LabState.Problem, "Certificate life", $"{ending.Count} computers have a certificate that ends within 90 days or has ended ({Names(ending.Select(computer => computer.Name))}).",
                    "Delete tls-cert.pfx in the Client's data folder, restart the service, and approve the new certificate (Approve certificate in the Host).")
                : longLived.Count > 0
                    ? new(LabState.Info, "Certificate life", $"{longLived.Count} of {certified.Count} computers have a certificate that lasts more than ten years. It is valid, but a stolen copy of its private key would also be useful that long; rotation of certificates is not built yet.")
                    : new(LabState.Ok, "Certificate life", $"All {certified.Count} certificates that were reported last for the next 90 days or more, and none for more than ten years."));

        // 4. Protocol skew.
        var withoutBinding = online.Where(computer => !Protocol.IsAtLeast(computer.Protocol, Protocol.ChannelBindingVersion)).ToList();
        var withoutDiscovery = online.Where(computer => !Protocol.IsAtLeast(computer.Protocol, Protocol.AuthenticatedDiscoveryVersion)).ToList();
        rows.Add(online.Count == 0
            ? new(LabState.Info, "Up-to-date Clients", "No computer answered, so this was not checked.")
            : withoutBinding.Count == 0 && withoutDiscovery.Count == 0
                ? new(LabState.Ok, "Up-to-date Clients", $"All {online.Count} computers speak protocol {Protocol.Version}: the key proof is bound to the TLS session and discovery is authenticated.")
                : new(LabState.Warning, "Up-to-date Clients",
                    $"{withoutDiscovery.Count} of {online.Count} computers are older than protocol {Protocol.AuthenticatedDiscoveryVersion} ({Names(withoutDiscovery.Select(computer => computer.Name))}); {withoutBinding.Count} are older than {Protocol.ChannelBindingVersion} and get the unbound key proof.",
                    "ClassroomCli update --all --client <signed ClassroomClient.exe> --yes"));

        // 5. The old, unauthenticated discovery question.
        var legacy = online.Where(computer => computer.LegacyDiscovery == true).ToList();
        var known = online.Count(computer => computer.LegacyDiscovery is not null);
        rows.Add(known == 0
            ? new(LabState.Info, "Discovery", "The computers did not say whether they still answer the old unauthenticated question (older Clients).")
            : legacy.Count == 0
                ? new(LabState.Ok, "Discovery", "No computer answers the old question: a stranger's discovery probe gets no answer.")
                : withoutDiscovery.Count == 0
                    ? new(LabState.Warning, "Discovery", $"{legacy.Count} computers still answer the old question with their name, client id and card address to anyone who asks.",
                        "Set \"allowLegacyDiscovery\": false in client.json on each (every Host is updated) and restart the service.")
                    : new(LabState.Info, "Discovery", $"{legacy.Count} computers still answer the old question; keep it until every Host and Client is updated."));

        // 6. Whether Clients insist on the bound proof.
        var requires = online.Count(computer => computer.RequiresBinding == true);
        var understands = online.Count(computer => computer.RequiresBinding is not null);
        rows.Add(understands == 0
            ? new(LabState.Info, "Bound key proof", "The computers did not say whether they require it (older Clients).")
            : requires == understands
                ? new(LabState.Ok, "Bound key proof", $"All {understands} computers refuse a key proof that is not bound to the TLS session.")
                : new(LabState.Info, "Bound key proof", $"{requires} of {understands} computers require the bound key proof; the others accept the plain one from an older Host.",
                    "Set \"requireChannelBinding\": true in client.json once every Host is updated."));

        // 7. What the computers have seen.
        var failures = computers.Sum(computer => computer.FailedHandshakes);
        var broken = computers.Where(computer => computer.BrokenChains > 0).ToList();
        rows.Add(broken.Count > 0
            ? new(LabState.Problem, "Security events", $"The security event log of {Names(broken.Select(computer => computer.Name))} is not intact.",
                "Run Classroom Client Health on that computer and read the line it names.")
            : failures >= 50
                ? new(LabState.Warning, "Security events", $"{failures} failed handshakes since the computers last started: a wrong key retrying, or someone guessing.",
                    "Read security-events.log on the computers with the most failures.")
                : new(LabState.Ok, "Security events", online.Count == 0
                    ? "No computer answered, so there is nothing to report."
                    : $"{failures} failed handshakes since the computers last started; no event log is broken."));

        // 8. Computers that could not be judged.
        var offline = computers.Where(computer => !computer.Online).ToList();
        if (offline.Count > 0)
            rows.Add(new(LabState.Info, "Computers not checked", $"{offline.Count} computers did not answer and are not judged above: {Names(offline.Select(computer => computer.Name))}."));

        return rows;
    }

    /// <summary>The worst state of any row.</summary>
    public static LabState Overall(IEnumerable<LabSecurityRow> rows) => rows.Select(row => row.State).DefaultIfEmpty(LabState.Ok).Max();

    static string Names(IEnumerable<string> names)
    {
        var list = names.Where(name => name.Length > 0).ToList();
        return list.Count <= 5 ? string.Join(", ", list) : string.Join(", ", list.Take(5)) + $" and {list.Count - 5} more";
    }

    /// <summary>The report as text for the command line.</summary>
    public static string Text(IReadOnlyList<LabSecurityRow> rows)
    {
        var text = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            text.AppendLine($"[{row.State.ToString().ToUpperInvariant(),-7}] {row.Title}: {row.Detail}");
            if (row.Action.Length > 0) text.AppendLine($"          to fix: {row.Action}");
        }
        text.AppendLine($"Overall: {Overall(rows)}");
        return text.ToString();
    }
}
