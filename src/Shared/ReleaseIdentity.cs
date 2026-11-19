// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassroomManager.Shared;

public enum ReleaseChannel { Internal, Customer }

/// <summary>What an update check decided. A code, not a sentence: the caller turns it into the text its user sees.</summary>
public enum ReleaseDecision
{
    Newer,
    AlreadyInstalled,
    NotNewer,
    ConflictingBytes,
    WrongAudience,
    WrongVariant,
    WrongSigner,
    NotACustomerRelease,
    NotBoundToThisFile,
    InvalidIdentity,
    OlderThanInstalledVersion,
}

/// <summary>
/// Who a release is, apart from the internal build that produced it: the product version the customer sees (<c>1.2.0</c>), the school it was
/// made for, an ordering number that only ever grows, and the digest and signer it is bound to. It is the update authority: a file name, a
/// file version or an editable manifest next to the program is not. The wire protocol version is a different thing and is not in here.
/// </summary>
public sealed record ReleaseIdentity(
    string PublicVersion,
    ReleaseChannel Channel,
    string Audience,
    string Variant,
    long Revision,
    string ReleaseId,
    string ExecutableSha256,
    string SignerSha256)
{
    /// <summary>The audience of a release that any school may take.</summary>
    public const string AnyAudience = "*";

    /// <summary>The largest value a Windows version resource can hold in one component.</summary>
    public const int MaxComponent = 65535;

    /// <summary>The Windows file version of a customer release: the public version with a zero fourth component.</summary>
    public string FileVersion => PublicVersion + ".0";

    /// <summary>The names of the fields that are not acceptable; empty when the identity is well formed.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!TryParsePublicVersion(PublicVersion, out _)) problems.Add("publicVersion");
        if (string.IsNullOrWhiteSpace(Audience) || Audience.Length > 64 || Audience.Any(char.IsControl)) problems.Add("audience");
        if (string.IsNullOrWhiteSpace(Variant) || Variant.Length > 64 || Variant.Any(char.IsControl)) problems.Add("variant");
        if (Revision < 1) problems.Add("revision");
        if (string.IsNullOrWhiteSpace(ReleaseId) || ReleaseId.Length > 64 || ReleaseId.Any(char.IsControl)) problems.Add("releaseId");
        if (!IsSha256(ExecutableSha256)) problems.Add("executableSha256");
        if (!IsSha256(SignerSha256)) problems.Add("signerSha256");
        return problems;
    }

    /// <summary>Three numbers, each one a Windows version component. Anything else (two numbers, four, a suffix) is not a public version.</summary>
    public static bool TryParsePublicVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var index = 0; index < 3; index++)
            if (parts[index].Length == 0 || parts[index].Length > 5 || !parts[index].All(char.IsAsciiDigit) ||
                !int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]) || numbers[index] > MaxComponent)
                return false;
        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    static bool IsSha256(string? text) => text is { Length: 64 } && text.All(char.IsAsciiHexDigit);

    /// <summary>True when this identity names exactly this file and signer. Ordering metadata that is not bound like this promotes nothing.</summary>
    public bool IsBoundTo(string actualSha256, string actualSignerSha256) =>
        string.Equals(ExecutableSha256, actualSha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(SignerSha256, actualSignerSha256, StringComparison.OrdinalIgnoreCase);

    const string EmbeddedMarker = "cm-release/1";

    /// <summary>
    /// The identity as one line for the program's version resource, which the program's signature covers. It leaves out the digest and the signer: the
    /// program cannot contain its own digest, and whoever reads it computes both from the file itself. Only customer releases are written this way.
    /// </summary>
    public static string Embedded(string publicVersion, string audience, string variant, long revision, string releaseId)
    {
        var fields = new[] { publicVersion, audience, variant, releaseId };
        if (fields.Any(field => field.Contains('|') || field.Any(char.IsControl))) throw new ArgumentException(null, nameof(publicVersion));
        return string.Join('|', EmbeddedMarker, publicVersion, "customer", audience, variant, revision.ToString(CultureInfo.InvariantCulture), releaseId);
    }

    public string ToEmbedded() => Channel == ReleaseChannel.Customer
        ? Embedded(PublicVersion, Audience, Variant, Revision, ReleaseId)
        : throw new InvalidOperationException();

    /// <summary>
    /// Reads the line a program carries and binds it to the digest and signer the reader computed from the file. Null when there is no such line,
    /// it is not exactly this format, or any field is unacceptable: a build that claims an identity it cannot back is not given the benefit of the doubt.
    /// </summary>
    public static ReleaseIdentity? TryParseEmbedded(string? text, string actualSha256, string actualSignerSha256)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 512 || !text.StartsWith(EmbeddedMarker + "|", StringComparison.Ordinal)) return null;
        var parts = text.Split('|');
        if (parts.Length != 7 || parts[0] != EmbeddedMarker || parts[2] != "customer") return null;
        if (!long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var revision)) return null;
        var identity = new ReleaseIdentity(parts[1], ReleaseChannel.Customer, parts[3], parts[4], revision, parts[6], actualSha256.ToLowerInvariant(), actualSignerSha256.ToLowerInvariant());
        return identity.Problems().Count == 0 ? identity : null;
    }

    public string ToJson() => new JsonObject
    {
        ["schema"] = 1,
        ["publicVersion"] = PublicVersion,
        ["channel"] = Channel.ToString().ToLowerInvariant(),
        ["audience"] = Audience,
        ["variant"] = Variant,
        ["revision"] = Revision,
        ["releaseId"] = ReleaseId,
        ["executableSha256"] = ExecutableSha256.ToLowerInvariant(),
        ["signerSha256"] = SignerSha256.ToLowerInvariant(),
    }.ToJsonString();

    /// <summary>Reads an identity; null when the text is not JSON, has another schema, lacks a field or fails <see cref="Problems"/>.</summary>
    public static ReleaseIdentity? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["schema"]?.GetValue<int>() != 1) return null;
            if (!Enum.TryParse<ReleaseChannel>(root["channel"]?.GetValue<string>(), ignoreCase: true, out var channel)) return null;
            var identity = new ReleaseIdentity(
                root["publicVersion"]!.GetValue<string>(), channel, root["audience"]!.GetValue<string>(), root["variant"]!.GetValue<string>(),
                root["revision"]!.GetValue<long>(), root["releaseId"]!.GetValue<string>(),
                root["executableSha256"]!.GetValue<string>().ToLowerInvariant(), root["signerSha256"]!.GetValue<string>().ToLowerInvariant());
            return identity.Problems().Count == 0 ? identity : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NullReferenceException) { return null; }
    }
}

/// <summary>The rules that decide whether one release may replace another.</summary>
public static class ReleaseOrdering
{
    /// <summary>
    /// Decides whether <paramref name="candidate"/> may replace what is installed.
    /// </summary>
    /// <param name="installed">The installed release's identity, or null for an installation that predates release identities.</param>
    /// <param name="installedFileVersion">The installed program's four-part file version; used only when <paramref name="installed"/> is null.</param>
    /// <param name="candidate">The identity that came with the new program.</param>
    /// <param name="actualSha256">The SHA-256 of the new program file, computed by the caller.</param>
    /// <param name="actualSignerSha256">The SHA-256 of the certificate that signed the new program, as verified by the caller.</param>
    /// <param name="recovery">An explicit recovery request: it allows an older revision and nothing else.</param>
    public static ReleaseDecision Decide(ReleaseIdentity? installed, Version? installedFileVersion, ReleaseIdentity candidate,
        string actualSha256, string actualSignerSha256, bool recovery = false)
    {
        if (candidate.Problems().Count > 0) return ReleaseDecision.InvalidIdentity;
        if (candidate.Channel != ReleaseChannel.Customer) return ReleaseDecision.NotACustomerRelease;
        if (!candidate.IsBoundTo(actualSha256, actualSignerSha256)) return ReleaseDecision.NotBoundToThisFile;
        ReleaseIdentity.TryParsePublicVersion(candidate.PublicVersion, out var candidateVersion);

        if (installed is null)
        {
            // An installation from before release identities has only a file version. A customer release of the same or a later public version
            // replaces it; a lower one does not. Nothing is known about its audience or variant, so the signer check by the caller is the guard.
            if (installedFileVersion is null) return ReleaseDecision.InvalidIdentity;
            var legacy = new Version(installedFileVersion.Major, installedFileVersion.Minor, Math.Max(installedFileVersion.Build, 0));
            return candidateVersion >= legacy || recovery ? ReleaseDecision.Newer : ReleaseDecision.OlderThanInstalledVersion;
        }

        if (installed.Problems().Count > 0) return ReleaseDecision.InvalidIdentity;
        if (!string.Equals(installed.Variant, candidate.Variant, StringComparison.Ordinal)) return ReleaseDecision.WrongVariant;
        if (!string.Equals(installed.SignerSha256, candidate.SignerSha256, StringComparison.OrdinalIgnoreCase)) return ReleaseDecision.WrongSigner;
        if (candidate.Audience != ReleaseIdentity.AnyAudience &&
            !string.Equals(candidate.Audience, installed.Audience, StringComparison.Ordinal)) return ReleaseDecision.WrongAudience;

        if (installed.Revision == candidate.Revision)
            return string.Equals(installed.ExecutableSha256, candidate.ExecutableSha256, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(installed.ReleaseId, candidate.ReleaseId, StringComparison.Ordinal)
                ? ReleaseDecision.AlreadyInstalled
                : ReleaseDecision.ConflictingBytes;
        if (string.Equals(installed.ReleaseId, candidate.ReleaseId, StringComparison.Ordinal)) return ReleaseDecision.ConflictingBytes;

        ReleaseIdentity.TryParsePublicVersion(installed.PublicVersion, out var installedVersion);
        var order = candidateVersion != installedVersion ? candidateVersion.CompareTo(installedVersion) : candidate.Revision.CompareTo(installed.Revision);
        if (order > 0) return ReleaseDecision.Newer;
        return recovery ? ReleaseDecision.Newer : ReleaseDecision.NotNewer;
    }
}
