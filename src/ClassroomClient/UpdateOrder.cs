// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>What the order check knows about one program file: its digest, its signer, its file version and the identity line in its version resource.</summary>
/// <param name="Embedded">The release line the program carries (see <see cref="ReleaseIdentity.Embedded"/>); null when it carries none.</param>
sealed record UpdateFile(string Sha256, string SignerSha256, Version? FileVersion, string? Embedded);

/// <summary>The outcome of the order check. <see cref="UseFileVersions"/> means neither side has a release identity, so the long-standing file-version rule applies.</summary>
sealed record UpdateOrderResult(bool Allowed, bool UseFileVersions, ErrorInfo? Error, string Message)
{
    public static readonly UpdateOrderResult Allow = new(true, false, null, "");
    public static readonly UpdateOrderResult FileVersionRule = new(true, true, null, "");
    public static UpdateOrderResult Refuse(ErrorInfo error, string message) => new(false, false, error, message);
}

/// <summary>
/// Whether an uploaded program may replace the installed one (T-218). A customer release is ordered by the identity inside its signed file, bound to the
/// digest and signer the Client computed itself, never by a file name or a loose manifest. Programs with no identity keep the file-version rule.
/// </summary>
static class UpdateOrder
{
    public const string ClaimPrefix = "cm-release/";

    /// <summary>The identity line a program carries in its version resource (the trademark field, covered by its signature), or null.</summary>
    public static string? ReadClaim(string path)
    {
        try
        {
            var claim = FileVersionInfo.GetVersionInfo(path).LegalTrademarks;
            return claim is not null && claim.StartsWith(ClaimPrefix, StringComparison.Ordinal) ? claim : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <param name="force">An explicit recovery: it allows an older revision, or an unidentified program over a customer release. Signer, audience, variant and digest checks stay.</param>
    public static UpdateOrderResult Evaluate(UpdateFile installed, UpdateFile candidate, bool force)
    {
        var candidateIdentity = ReleaseIdentity.TryParseEmbedded(candidate.Embedded, candidate.Sha256, candidate.SignerSha256);
        if (candidate.Embedded is not null && candidateIdentity is null)
            return UpdateOrderResult.Refuse(ErrorCatalog.UpdateFailedChecks, "The update claims a release identity that is not valid, so it was not installed.");

        var installedIdentity = ReleaseIdentity.TryParseEmbedded(installed.Embedded, installed.Sha256, installed.SignerSha256);
        if (candidateIdentity is null)
            return installedIdentity is null || force
                ? UpdateOrderResult.FileVersionRule
                : UpdateOrderResult.Refuse(ErrorCatalog.UpdateFailedChecks,
                    "The installed Client is a customer release and the update carries no release identity, so its order cannot be checked. Pass force to install it anyway.");

        var decision = ReleaseOrdering.Decide(installedIdentity, installed.FileVersion, candidateIdentity, candidate.Sha256, candidate.SignerSha256, force);
        return decision switch
        {
            ReleaseDecision.Newer => UpdateOrderResult.Allow,
            ReleaseDecision.AlreadyInstalled => UpdateOrderResult.Refuse(ErrorCatalog.UpdateNotNewer, "This release is already installed."),
            ReleaseDecision.NotNewer or ReleaseDecision.OlderThanInstalledVersion => UpdateOrderResult.Refuse(ErrorCatalog.UpdateNotNewer,
                "The uploaded release is not newer than the installed one. Pass force to install it anyway."),
            ReleaseDecision.WrongAudience => UpdateOrderResult.Refuse(ErrorCatalog.UpdateFailedChecks, "The update was made for another school, so it was not installed."),
            ReleaseDecision.WrongVariant => UpdateOrderResult.Refuse(ErrorCatalog.UpdateFailedChecks, "The update is for another architecture or runtime, so it was not installed."),
            ReleaseDecision.WrongSigner => UpdateOrderResult.Refuse(ErrorCatalog.UpdateFailedChecks, "The update is signed by a different signer than the installed Client, so it was not installed."),
            ReleaseDecision.ConflictingBytes => UpdateOrderResult.Refuse(ErrorCatalog.UpdateFailedChecks, "The update reuses a release number or id that belongs to other bytes, so it was not installed."),
            _ => UpdateOrderResult.Refuse(ErrorCatalog.UpdateFailedChecks, "The update's release identity is not acceptable, so it was not installed."),
        };
    }
}
