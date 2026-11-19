// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>A newly installed computer that announced its certificate in answer to this Host's signed probe (T-185, enrolment mode).</summary>
public sealed record EnrolmentOffer(IPAddress Address, int Port, int ProtocolMinor, string CertificateSha256);

public enum AutoEnrolAction
{
    /// <summary>No approval exists at this address: the announced certificate may be approved.</summary>
    Enrol,
    /// <summary>The announced certificate is already the approved one.</summary>
    AlreadyApproved,
    /// <summary>The address already has a different approval. Never replaced by itself: <c>ClassroomCli trust-check</c> shows what to do.</summary>
    DifferentApproval,
    /// <summary>Not enrolled: the certificate it presents is not the one it announced, or too many in one scan.</summary>
    Refused,
}

public sealed record AutoEnrolDecision(EnrolmentOffer Offer, AutoEnrolAction Action, string Text);

/// <summary>
/// Adds a new computer by itself (T-185). The announcement is signed with the discovery key derived from the lab key, so only a holder of
/// that key could have made it, which is the same trust as an enrolment file (T-200) without carrying a file around. On top of that the
/// Host looks at the certificate the computer really presents (nothing is sent over that look) and enrols only when it is the announced
/// one. An address that already has an approval is never changed.
/// </summary>
public static class AutoEnrol
{
    /// <summary>More than this in one scan is not a lab being installed but something wrong; the rest wait for a person.</summary>
    public const int MaxPerScan = 50;

    public static List<AutoEnrolDecision> Plan(HostSettings settings, IReadOnlyList<EnrolmentOffer> offers)
    {
        var decisions = new List<AutoEnrolDecision>();
        foreach (var offer in offers.DistinctBy(offer => offer.Address.ToString()))
        {
            var address = offer.Address.ToString();
            var here = (settings.TrustedTlsEndpoints ?? []).Where(endpoint => endpoint.Port == offer.Port && endpoint.Address.Trim().Equals(address, StringComparison.OrdinalIgnoreCase)).ToList();
            if (here.Any(endpoint => (endpoint.CertificateSha256 ?? []).Any(pin => pin.Equals(offer.CertificateSha256, StringComparison.OrdinalIgnoreCase))))
                decisions.Add(new(offer, AutoEnrolAction.AlreadyApproved, $"{address} announces the certificate that is already approved."));
            else if (here.Count > 0)
                decisions.Add(new(offer, AutoEnrolAction.DifferentApproval,
                    $"{address} announces a certificate that is not the approved one, so nothing was changed. If this is a reinstalled computer, run ClassroomCli trust-check --live to see and approve it."));
            else
                decisions.Add(new(offer, AutoEnrolAction.Enrol, $"{address} is new and announces its certificate."));
        }
        return decisions;
    }

    /// <summary>
    /// Enrols what <see cref="Plan"/> allows and returns the decisions as they turned out. <paramref name="presented"/> returns the
    /// SHA-256 of the certificate a computer really presents. When <paramref name="addComputers"/> is set the computer is also put in the
    /// saved list (a window adds it to its own list instead). The caller saves the settings.
    /// </summary>
    public static async Task<List<AutoEnrolDecision>> ApplyAsync(HostSettings settings, IReadOnlyList<AutoEnrolDecision> plan,
        Func<EnrolmentOffer, CancellationToken, Task<string>> presented, bool addComputers, CancellationToken ct = default,
        Action<EnrolmentOffer>? onEnrolled = null)
    {
        var result = new List<AutoEnrolDecision>();
        var enrolled = 0;
        foreach (var decision in plan)
        {
            if (decision.Action != AutoEnrolAction.Enrol) { result.Add(decision); continue; }
            var offer = decision.Offer;
            var address = offer.Address.ToString();
            if (enrolled >= MaxPerScan)
            {
                result.Add(decision with { Action = AutoEnrolAction.Refused, Text = $"{address} was not enrolled: more than {MaxPerScan} new computers in one scan. Approve the rest with ClassroomCli trust-check." });
                continue;
            }
            var seen = await presented(offer, ct);
            if (!seen.Equals(offer.CertificateSha256, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(decision with
                {
                    Action = AutoEnrolAction.Refused,
                    Text = seen.Length == 0
                        ? $"{address} was not enrolled: it did not answer a look at its certificate."
                        : $"{address} was not enrolled: the certificate it presents is not the one it announced.",
                });
                continue;
            }
            // An approval may have changed while the certificate was awaited. Never replace it from a stale plan.
            var current = Plan(settings, [offer]).Single();
            if (current.Action != AutoEnrolAction.Enrol) { result.Add(current); continue; }
            settings.EnrollTrust(address, offer.Port, "", [offer.CertificateSha256]);
            if (addComputers && !settings.Computers.Any(computer => computer.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
                settings.Computers.Add(new SavedComputer { Address = address, Name = address, Port = offer.Port == settings.TcpPort ? 0 : offer.Port });
            onEnrolled?.Invoke(offer);
            enrolled++;
            result.Add(decision with { Text = $"{address} was added and its certificate approved (announced under this lab's key and seen on the connection)." });
        }
        return result;
    }

    /// <summary>The look-only connection of the trust tools, as the way to see which certificate a computer really presents.</summary>
    public static async Task<string> PresentedAsync(HostSettings settings, EnrolmentOffer offer, CancellationToken ct) =>
        (await TrustCheckCli.ProbeAsync(new SavedComputer { Address = offer.Address.ToString(), Port = offer.Port }, settings, ct)).Presented;
}
