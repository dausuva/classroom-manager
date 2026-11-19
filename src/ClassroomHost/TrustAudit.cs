// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>A discovery answer: who says it is at an address. Unauthenticated, so it only ever points at what to look at.</summary>
public sealed record AuditSighting(string Address, string Mac, string ClientId, string Name);

/// <summary>
/// What a look-only connection to a saved computer showed: the certificate it presents (nothing is sent over that
/// connection) and the network card address the network itself reports for the address (ARP). Empty when unknown.
/// </summary>
public sealed record AuditProbe(string Address, int Port, string Presented, string ArpMac);

public enum AuditState { NotChecked, Ok, NeedsApproval, CertOfAnother, MovedAddress, AddressClash, Offline }

public sealed record AuditLine(SavedComputer Computer, AuditState State, string Detail, string Command);

/// <summary>A change that is safe to make by itself: it names whose an approval is, follows a moved computer, or drops one that is provably another computer's.</summary>
public abstract record AuditFix(string Describe);
public sealed record ClaimFix(string Address, int Port, string Mac, string Name) : AuditFix($"record the network card of {Name} on its approval at {Address}");
public sealed record MoveFix(SavedComputer Computer, string NewAddress, string Name) : AuditFix($"move {Name} from {Computer.Address} to {NewAddress}, with its approval");
public sealed record RemoveFix(TrustedTlsEndpoint Endpoint, string Reason) : AuditFix($"remove the approval at {Endpoint.Address}: {Reason}");

public sealed record AuditReport(List<AuditLine> Lines, List<string> Notes, List<AuditFix> Fixes)
{
    public bool Healthy => Lines.All(line => line.State is AuditState.Ok or AuditState.NotChecked) && Notes.Count == 0;
}

/// <summary>
/// Finds, from the Host's saved computers and approved certificates (and, when asked, what the network shows now),
/// the computers whose approval no longer fits: another computer took their address, they moved, or the approval
/// at their address belongs to one that left. Certificates are never approved here: an approval comes only from
/// <see cref="Approve"/>, which needs the fingerprint a person read on that computer.
/// </summary>
public static class TrustAudit
{
    /// <summary>How many leading hex digits of a fingerprint a person must give to approve it from what the computer presents.</summary>
    public const int MinimumCheckDigits = 20;

    public static AuditReport Analyze(HostSettings settings, IReadOnlyList<AuditSighting> sightings, IReadOnlyList<AuditProbe> probes, bool live)
    {
        var lines = new List<AuditLine>();
        var notes = new List<string>();
        var fixes = new List<AuditFix>();
        var computers = settings.Computers;
        int PortOf(SavedComputer c) => c.Port > 0 ? c.Port : settings.TcpPort;
        bool At(string a, string b) => a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);
        string Show(SavedComputer c) => c.Name.Length > 0 ? c.Name : c.Address;

        foreach (var group in computers.GroupBy(c => c.Address.Trim(), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            notes.Add($"{string.Join(" and ", group.Select(Show))} are both saved at {group.Key}; only one of them can be there.");

        foreach (var computer in computers)
        {
            var port = PortOf(computer);
            var card = ComputerIdentity.NormalizeMac(computer.Mac);
            var pins = settings.TlsPins(computer.Address, port, card);
            var probe = probes.FirstOrDefault(p => At(p.Address, computer.Address) && p.Port == port);
            var heard = sightings.FirstOrDefault(s => At(s.Address, computer.Address));
            string Command(string presented) => presented.Length == 0 ? "" :
                $"ClassroomCli trust-check --approve --computer \"{Show(computer)}\" --check {presented[..MinimumCheckDigits]} --yes   (after comparing {presented[..MinimumCheckDigits]}... with Classroom Client Health on that computer)";

            if (!live)
            {
                var claimable = card.Length > 0 && settings.TrustedTlsEndpoints.Any(e => e.Port == port && e.Mac.Length == 0 && At(e.Address, computer.Address));
                lines.Add(new AuditLine(computer, AuditState.NotChecked,
                    (pins.Count == 0 ? "no approved certificate" : $"{pins.Count} approved certificate(s)") +
                    (card.Length == 0 ? "; its network card address is not known yet" : claimable ? "; the approval does not name its network card yet" : ""), ""));
                continue;
            }

            // Who really holds the address now: the computer's own answer, else what the network says.
            var holderCard = ComputerIdentity.NormalizeMac(heard?.Mac);
            if (holderCard.Length == 0) holderCard = ComputerIdentity.NormalizeMac(probe?.ArpMac);
            var movedTo = card.Length == 0 && computer.ClientId.Length == 0 ? [] : sightings
                .Where(s => !At(s.Address, computer.Address) && ComputerIdentity.StableMatch(computer.ClientId, computer.Mac, s.ClientId, s.Mac)).ToList();
            var presented = probe?.Presented ?? "";

            if (card.Length > 0 && holderCard.Length > 0 && holderCard != card)
            {
                var other = computers.FirstOrDefault(c => c != computer && ComputerIdentity.NormalizeMac(c.Mac) == holderCard);
                var who = other is not null ? Show(other) : heard?.Name is { Length: > 0 } name ? name : "another computer";
                var detail = $"{who} (card {ComputerIdentity.FormatMac(holderCard)}) answers at {computer.Address}, not {Show(computer)} (card {ComputerIdentity.FormatMac(card)}).";
                if (movedTo.Count == 1)
                {
                    detail += $" {Show(computer)} answers at {movedTo[0].Address}.";
                    fixes.Add(new MoveFix(computer, movedTo[0].Address, Show(computer)));
                }
                else detail += $" {Show(computer)} was not found on the network; switch it on, or give both computers fixed addresses.";
                lines.Add(new AuditLine(computer, AuditState.AddressClash, detail, ""));
                continue;
            }

            if (presented.Length == 0 && movedTo.Count == 1)
            {
                lines.Add(new AuditLine(computer, AuditState.MovedAddress, $"does not answer at {computer.Address}; it answers at {movedTo[0].Address}.", ""));
                fixes.Add(new MoveFix(computer, movedTo[0].Address, Show(computer)));
                continue;
            }
            if (presented.Length == 0)
            {
                lines.Add(new AuditLine(computer, AuditState.Offline, $"does not answer at {computer.Address} (switched off, or not on this network).", ""));
                continue;
            }

            if (pins.Contains(presented, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add(new AuditLine(computer, AuditState.Ok, "its certificate is the approved one" + (card.Length == 0 ? "; its network card address is not known yet, so the approval cannot name it" : ""), ""));
                if (card.Length > 0 && settings.TrustedTlsEndpoints.Any(e => e.Port == port && e.Mac.Length == 0 && At(e.Address, computer.Address)))
                    fixes.Add(new ClaimFix(computer.Address, port, card, Show(computer)));
                continue;
            }

            var elsewhere = settings.TrustedTlsEndpoints.Where(e => e.Port == port && (e.CertificateSha256 ?? []).Contains(presented, StringComparer.OrdinalIgnoreCase)).ToList();
            if (elsewhere.Count > 0)
            {
                var places = string.Join(", ", elsewhere.Select(e => e.Address + (e.Mac.Length > 0 ? $" (card {ComputerIdentity.FormatMac(e.Mac)})" : "")));
                lines.Add(new AuditLine(computer, AuditState.CertOfAnother,
                    $"presents {presented[..16]}..., which is approved for {places}; the approval at {computer.Address} is a different one.", Command(presented)));
            }
            else
                lines.Add(new AuditLine(computer, AuditState.NeedsApproval, $"presents {presented[..16]}..., which nobody approved.", Command(presented)));

            // A leftover at this address that is provably another computer's: its certificate is approved for another card.
            foreach (var entry in settings.TrustedTlsEndpoints.Where(e => e.Port == port && e.Mac.Length == 0 && At(e.Address, computer.Address)).ToList())
            {
                var pinsOfEntry = entry.CertificateSha256 ?? [];
                var owner = settings.TrustedTlsEndpoints.FirstOrDefault(o => o != entry && o.Port == port && o.Mac.Length > 0 && ComputerIdentity.NormalizeMac(o.Mac) != card
                    && pinsOfEntry.Count > 0 && pinsOfEntry.All(pin => (o.CertificateSha256 ?? []).Contains(pin, StringComparer.OrdinalIgnoreCase)));
                if (owner is not null && !pinsOfEntry.Contains(presented, StringComparer.OrdinalIgnoreCase))
                    fixes.Add(new RemoveFix(entry, $"its certificate belongs to the computer with card {ComputerIdentity.FormatMac(owner.Mac)} ({owner.Address}), and {Show(computer)} presents a different one"));
            }
        }

        foreach (var entry in settings.TrustedTlsEndpoints)
            if (!computers.Any(c => At(c.Address, entry.Address) && PortOf(c) == entry.Port) &&
                !(entry.Mac.Length > 0 && computers.Any(c => ComputerIdentity.NormalizeMac(c.Mac) == ComputerIdentity.NormalizeMac(entry.Mac))))
                notes.Add($"An approval for {entry.Address}:{entry.Port}{(entry.Mac.Length > 0 ? $" (card {ComputerIdentity.FormatMac(entry.Mac)})" : "")} belongs to no saved computer; it can be removed with: ClassroomCli trust --computer <name> --clear --yes after saving that address again, or by hand.");
        foreach (var group in settings.TrustedTlsEndpoints.Where(e => e.Mac.Length == 0).SelectMany(e => (e.CertificateSha256 ?? []).Select(pin => (pin, e.Address)))
                     .GroupBy(x => x.pin, StringComparer.OrdinalIgnoreCase).Where(group => group.Select(x => x.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            notes.Add($"One certificate ({group.Key[..16]}...) is approved for several addresses with no network card named: {string.Join(", ", group.Select(x => x.Address).Distinct())}. A computer can only be at one; run trust-check --live.");

        return new AuditReport(lines, notes.Distinct().ToList(), fixes);
    }

    /// <summary>Makes the safe fixes in the settings. Returns how many were applied.</summary>
    public static int Apply(HostSettings settings, IEnumerable<AuditFix> fixes)
    {
        var applied = 0;
        foreach (var fix in fixes)
            switch (fix)
            {
                case ClaimFix claim when settings.ClaimTrust(claim.Address, claim.Port, claim.Mac): applied++; break;
                case MoveFix move when settings.Computers.Contains(move.Computer):
                    var port = move.Computer.Port > 0 ? move.Computer.Port : settings.TcpPort;
                    settings.MoveTrust(move.Computer.Mac, move.NewAddress, port);
                    move.Computer.Address = move.NewAddress;
                    applied++;
                    break;
                case RemoveFix remove when settings.TrustedTlsEndpoints.Remove(remove.Endpoint): applied++; break;
            }
        return applied;
    }

    /// <summary>
    /// Approves what a computer presents now, but only when the person typed the start of its fingerprint as
    /// Classroom Client Health shows it on that computer. Returns the full fingerprint, or throws when it does not match.
    /// </summary>
    public static string Approve(HostSettings settings, SavedComputer computer, string presented, string typed)
    {
        var digits = new string((typed ?? "").Where(Uri.IsHexDigit).ToArray());
        if (!TlsEndpointTrust.IsFingerprint(presented))
            throw new InvalidOperationException("The computer presented no certificate to approve.");
        if (digits.Length < MinimumCheckDigits)
            throw new CliUsageException($"--check needs at least {MinimumCheckDigits} hex digits of the fingerprint Classroom Client Health shows on that computer.");
        if (!presented.StartsWith(digits, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("What the computer presents now does not start with the digits you gave, so it is not the certificate you checked. Nothing was approved.");
        settings.EnrollTrust(computer.Address, computer.Port > 0 ? computer.Port : settings.TcpPort, computer.Mac, [presented.ToUpperInvariant()]);
        return presented.ToUpperInvariant();
    }

    public static string Text(AuditReport report, bool live)
    {
        var text = new System.Text.StringBuilder();
        foreach (var line in report.Lines)
        {
            var name = line.Computer.Name.Length > 0 ? line.Computer.Name : line.Computer.Address;
            text.AppendLine($"[{Label(line.State),-8}] {name}  {line.Computer.Address}  {ComputerIdentity.FormatMac(line.Computer.Mac)}  {line.Detail}");
            if (line.Command.Length > 0) text.AppendLine("           " + line.Command);
        }
        foreach (var note in report.Notes) text.AppendLine("[note    ] " + note);
        if (report.Fixes.Count > 0)
        {
            text.AppendLine().AppendLine("Safe fixes (add --fix --yes with the Host closed; nothing new is trusted):");
            foreach (var fix in report.Fixes) text.AppendLine("  - " + fix.Describe);
        }
        else if (live && report.Healthy) text.AppendLine().AppendLine("Everything fits.");
        if (!live) text.AppendLine().AppendLine("Nothing was checked on the network. Add --live to look at every computer.");
        return text.ToString().TrimEnd();
    }

    static string Label(AuditState state) => state switch
    {
        AuditState.Ok => "ok", AuditState.NeedsApproval => "approve", AuditState.CertOfAnother => "other's",
        AuditState.MovedAddress => "moved", AuditState.AddressClash => "CLASH", AuditState.Offline => "offline", _ => "saved",
    };

    /// <summary>The table a router needs to give every computer a fixed address: name, network card, address.</summary>
    public static string Reservations(HostSettings settings) => string.Join(Environment.NewLine,
        settings.Computers.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => $"{(c.Name.Length > 0 ? c.Name : c.Address)}\t{ComputerIdentity.FormatMac(c.Mac)}\t{c.Address}"));
}
