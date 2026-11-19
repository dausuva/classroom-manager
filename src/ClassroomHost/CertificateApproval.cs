// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public enum ApprovalKind { Unreachable, AlreadyApproved, New, Changed, ApprovedElsewhere }

/// <summary>What the Host found at an address and what it would mean to trust it. <see cref="Warning"/> is shown in red.</summary>
public sealed record ApprovalAssessment(ApprovalKind Kind, string Headline, IReadOnlyList<string> Facts, string Warning)
{
    public bool CanApprove => Kind is ApprovalKind.New or ApprovalKind.Changed or ApprovalKind.ApprovedElsewhere;
}

/// <summary>
/// The "do you trust this certificate?" step, in the manner of the first SSH connection: the Host looks at what the computer presents
/// (the look-only connection of the trust tools, no key or command is sent), shows it with the facts around it, and a person answers
/// yes or no. Nothing is typed. The decision is plain functions so it can be tested without a window.
/// </summary>
public static class CertificateApproval
{
    /// <summary>The fingerprint in groups of eight digits, so that it can be read against Classroom Client Health on that computer.</summary>
    public static string Spaced(string fingerprint)
    {
        var digits = new string((fingerprint ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return string.Join(" ", Enumerable.Range(0, (digits.Length + 7) / 8).Select(i => digits.Substring(i * 8, Math.Min(8, digits.Length - i * 8))));
    }

    public static ApprovalAssessment Assess(HostSettings settings, SavedComputer computer, string presented, string arpMac)
    {
        var port = computer.Port > 0 ? computer.Port : settings.TcpPort;
        var facts = new List<string>();
        var name = computer.Name.Length > 0 ? computer.Name : computer.Address;
        if (!TlsEndpointTrust.IsFingerprint(presented))
            return new ApprovalAssessment(ApprovalKind.Unreachable, $"{name} ({computer.Address}) did not answer with a certificate.",
                ["It may be switched off, on another network, or its Classroom Client service is not running. Nothing can be approved."], "");

        var card = ComputerIdentity.NormalizeMac(computer.Mac);
        var heard = ComputerIdentity.NormalizeMac(arpMac);
        if (heard.Length > 0) facts.Add($"Network card on the network now: {ComputerIdentity.FormatMac(heard)}");
        else facts.Add("Network card: not known (the computer is not on this network segment).");

        var pins = (settings.TrustedTlsEndpoints ?? [])
            .Where(endpoint => endpoint.Port == port && endpoint.Address.Trim().Equals(computer.Address.Trim(), StringComparison.OrdinalIgnoreCase)
                && (endpoint.Mac.Length == 0 || card.Length == 0 || ComputerIdentity.NormalizeMac(endpoint.Mac) == card))
            .SelectMany(endpoint => endpoint.CertificateSha256 ?? []).ToList();
        if (pins.Contains(presented, StringComparer.OrdinalIgnoreCase))
            return new ApprovalAssessment(ApprovalKind.AlreadyApproved, $"{name} ({computer.Address}) presents the certificate you already approved.", facts,
                "");

        string warning = "";
        if (card.Length > 0 && heard.Length > 0 && card != heard)
            warning = $"The saved computer has the network card {ComputerIdentity.FormatMac(card)}, but another card answers at this address. It may be a different computer.";

        if (pins.Count > 0)
        {
            facts.Add($"An earlier approval for this address names a different certificate ({pins[0][..16]}...).");
            return new ApprovalAssessment(ApprovalKind.Changed, $"The certificate of {name} ({computer.Address}) has CHANGED.", facts,
                (warning.Length > 0 ? warning + " " : "") + "Trust it only when the computer was reinstalled or its Classroom Client was set up again, and you know it.");
        }

        var elsewhere = (settings.TrustedTlsEndpoints ?? []).Where(endpoint => (endpoint.CertificateSha256 ?? []).Contains(presented, StringComparer.OrdinalIgnoreCase)).ToList();
        if (elsewhere.Count > 0)
        {
            facts.Add("The same certificate is already approved for: " + string.Join(", ", elsewhere.Select(endpoint => endpoint.Address)));
            return new ApprovalAssessment(ApprovalKind.ApprovedElsewhere, $"{name} ({computer.Address}) presents a certificate approved for another address.", facts,
                (warning.Length > 0 ? warning + " " : "") + "That is expected only when the computer moved to this address.");
        }

        facts.Add("No certificate was approved for this address before.");
        return new ApprovalAssessment(ApprovalKind.New, $"The identity of {name} ({computer.Address}) cannot be checked yet.", facts, warning);
    }

    /// <summary>
    /// Records the approval for the certificate that was shown. The caller passes the fingerprint the person saw; it is approved only
    /// when it is still what the computer presents, so a swap between "show" and "yes" approves nothing.
    /// </summary>
    public static bool Approve(HostSettings settings, SavedComputer computer, string shown, string presentedNow, string arpMac)
    {
        if (!TlsEndpointTrust.IsFingerprint(shown) || !shown.Equals(presentedNow, StringComparison.OrdinalIgnoreCase)) return false;
        var port = computer.Port > 0 ? computer.Port : settings.TcpPort;
        var card = ComputerIdentity.NormalizeMac(computer.Mac);
        if (card.Length == 0) card = ComputerIdentity.NormalizeMac(arpMac);
        settings.EnrollTrust(computer.Address, port, card, [shown.ToUpperInvariant()]);
        return true;
    }
}
