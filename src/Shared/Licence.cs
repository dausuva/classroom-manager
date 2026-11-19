// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassroomManager.Shared;

/// <summary>
/// What a licence grants (T-213): one school, a number of computers, one year from the day of agreement. The licence never stops the
/// program: it decides only whether the school gets signed builds and trust updates, so an expired one brings Windows' untrusted-publisher
/// prompts on the next install, which a renewal (new trust update and Client update) takes away.
/// </summary>
public sealed record LicenceTerms
{
    /// <summary>The licence number, such as "CM-2026-0007".</summary>
    public string Id { get; init; } = "";
    /// <summary>The school's name as it appears on the agreement.</summary>
    public string School { get; init; } = "";
    /// <summary>The school's registration code, when it has one.</summary>
    public string SchoolCode { get; init; } = "";
    /// <summary>The distribution profile of the school (its builds and signing certificate).</summary>
    public string CustomerId { get; init; } = "";
    public int Computers { get; init; }
    /// <summary>A separate agreement: 50 computers or more ("contact us"), or terms that differ from the standard licence.</summary>
    public bool Custom { get; init; }
    /// <summary>The day the agreement was made; the licence runs a year from it.</summary>
    public DateOnly Agreement { get; init; }
    /// <summary>The last day of the licence, included.</summary>
    public DateOnly Ends { get; init; }
    public DateTime IssuedUtc { get; init; }
    public string Issuer { get; init; } = "Dausuva MB";
    /// <summary>
    /// The <see cref="ComputerId"/> of the one Host this licence is for (T-262), or "" for a licence of an earlier kind that any Host accepts.
    /// A school has up to <see cref="Licences.HostsPerSchool"/> Hosts, each with its own licence file.
    /// </summary>
    public string HostId { get; init; } = "";
    /// <summary>What the school calls that Host (such as "Room 12, teacher PC"), for the licence list and the Host's About window; "" when not named.</summary>
    public string HostName { get; init; } = "";

    /// <summary>The licence's own text in a fixed order, which is what is signed and checked.</summary>
    public byte[] Canonical()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            // A licence tied to a computer is version 2; one that is not keeps exactly the text it was signed with as version 1.
            json.WriteNumber("v", HostId.Length > 0 ? 2 : 1);
            json.WriteString("id", Id);
            json.WriteString("school", School);
            json.WriteString("schoolCode", SchoolCode);
            json.WriteString("customerId", CustomerId);
            json.WriteNumber("computers", Computers);
            json.WriteBoolean("custom", Custom);
            json.WriteString("agreement", Agreement.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            json.WriteString("ends", Ends.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            json.WriteString("issuedUtc", IssuedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            json.WriteString("issuer", Issuer);
            if (HostId.Length > 0)
            {
                json.WriteString("hostId", HostId);
                json.WriteString("hostName", HostName);
            }
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    internal static LicenceTerms FromJson(JsonObject o)
    {
        string Text(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        DateOnly Day(string key) => DateOnly.TryParseExact(Text(key), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : throw new FormatException($"The licence has no valid '{key}' date.");
        if (o["v"] is not JsonValue version || !version.TryGetValue<int>(out var v) || v is not (1 or 2))
            throw new FormatException("The licence is of a version this program does not know.");
        var hostId = Text("hostId");
        if (v == 2 && ComputerId.Normalize(hostId) is null) throw new FormatException("The licence names no valid computer.");
        if (v == 1 && (hostId.Length > 0 || o["hostName"] is not null)) throw new FormatException("The licence names a computer but is not of the version that does.");
        return new LicenceTerms
        {
            Id = Text("id"),
            School = Text("school"),
            SchoolCode = Text("schoolCode"),
            CustomerId = Text("customerId"),
            Computers = o["computers"] is JsonValue c && c.TryGetValue<int>(out var computers) ? computers : 0,
            Custom = o["custom"] is JsonValue cu && cu.TryGetValue<bool>(out var custom) && custom,
            Agreement = Day("agreement"),
            Ends = Day("ends"),
            IssuedUtc = DateTime.TryParse(Text("issuedUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var issued) ? DateTime.SpecifyKind(issued, DateTimeKind.Utc) : default,
            Issuer = Text("issuer"),
            HostId = v == 2 ? ComputerId.Normalize(hostId)! : "",
            HostName = v == 2 ? Text("hostName") : "",
        };
    }
}

/// <summary>Where a licence stands on a given day.</summary>
public enum LicenceState
{
    /// <summary>No licence file.</summary>
    Missing,
    /// <summary>The file is damaged, changed or signed by a key this program does not trust.</summary>
    Invalid,
    Active,
    /// <summary>Active, with <see cref="Licences.RenewalNoticeDays"/> or fewer days left.</summary>
    EndingSoon,
    /// <summary>Past its last day. The program keeps working; signed updates stop until it is renewed.</summary>
    Expired,
}

/// <summary>The result of checking a licence file.</summary>
public sealed record LicenceCheck(LicenceState State, LicenceTerms? Terms, int DaysLeft, string Problem)
{
    public bool Signed => State is LicenceState.Active or LicenceState.EndingSoon or LicenceState.Expired;

    /// <summary>The file is intact as far as can be told, but signed with a key this build does not list (an older Host, or a new key).</summary>
    public bool KeyUnknown { get; init; }

    /// <summary>The licence is genuine but was issued for another computer than the one it was checked on (T-262).</summary>
    public bool WrongComputer { get; init; }
}

/// <summary>Making, signing and checking licences (T-213). ECDSA P-256 with SHA-256; the signature is in IEEE P1363 form.</summary>
public static class Licences
{
    /// <summary>A standard licence covers fewer than this many computers; more need a separate agreement.</summary>
    public const int StandardLimit = 50;
    /// <summary>How many days before its end a licence counts as ending soon.</summary>
    public const int RenewalNoticeDays = 30;
    /// <summary>How many Hosts (teacher computers) one school can have licences for.</summary>
    public const int HostsPerSchool = 3;
    public const string FileExtension = ".cmlicence";
    const string Kind = "classroom-manager-licence";

    /// <summary>
    /// The last day of a licence agreed on <paramref name="agreement"/>: the day before the same date a year later. Agreed on 29 February,
    /// the year ends on 28 February (its anniversary counts as 1 March), not a day early.
    /// </summary>
    public static DateOnly EndFor(DateOnly agreement)
    {
        var anniversary = agreement.AddYears(1);
        if (agreement is { Month: 2, Day: 29 } && anniversary.Day == 28) anniversary = anniversary.AddDays(1);
        return anniversary.AddDays(-1);
    }

    /// <summary>Why these terms cannot be issued, or null when they can.</summary>
    public static string? Problem(LicenceTerms terms)
    {
        if (terms.Id.Trim().Length == 0) return "The licence has no number.";
        if (terms.School.Trim().Length == 0) return "The licence names no school.";
        if (terms.Computers < 1) return "The licence must cover at least one computer.";
        if (!terms.Custom && terms.Computers >= StandardLimit)
            return $"A standard licence covers fewer than {StandardLimit} computers; {terms.Computers} need a separate agreement (contact us).";
        if (terms.HostId.Length > 0 && ComputerId.Normalize(terms.HostId) != terms.HostId) return "The licence names no valid computer.";
        if (terms.HostName.Length > 100) return "The computer's name is too long (100 characters at most).";
        if (terms.Ends < terms.Agreement) return "The licence ends before it starts.";
        if (!terms.Custom && terms.Ends != EndFor(terms.Agreement)) return "A standard licence runs exactly one year from the day of agreement.";
        return null;
    }

    /// <summary>The short id of a public key: the first 16 hex digits of the SHA-256 of its SubjectPublicKeyInfo.</summary>
    public static string KeyId(byte[] subjectPublicKeyInfo) => Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo))[..16];

    /// <summary>The licence file: the terms, the id of the signing key and the signature.</summary>
    public static string Sign(LicenceTerms terms, ECDsa key)
    {
        if (Problem(terms) is { } problem) throw new ArgumentException(problem, nameof(terms));
        var canonical = terms.Canonical();
        var signature = key.SignData(canonical, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var file = new JsonObject
        {
            ["kind"] = Kind,
            ["licence"] = JsonNode.Parse(canonical),
            ["keyId"] = KeyId(key.ExportSubjectPublicKeyInfo()),
            ["signature"] = Convert.ToBase64String(signature),
        };
        return file.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Checks a licence file against the trusted public keys (SubjectPublicKeyInfo, by <see cref="KeyId"/>) on <paramref name="today"/>.
    /// Never throws for a bad file: the result says what is wrong. With <paramref name="computerId"/> (the id of the computer the check is made on)
    /// a licence tied to another computer is not valid; without it the computer is not compared (Classroom Desk checks files it did not issue).
    /// </summary>
    public static LicenceCheck Check(string? fileText, IReadOnlyDictionary<string, byte[]> trustedKeys, DateOnly today, string? computerId = null)
    {
        if (string.IsNullOrWhiteSpace(fileText)) return new LicenceCheck(LicenceState.Missing, null, 0, "");
        LicenceTerms terms;
        try
        {
            if (JsonNode.Parse(fileText) is not JsonObject file || file["kind"]?.GetValue<string>() != Kind || file["licence"] is not JsonObject body)
                return new LicenceCheck(LicenceState.Invalid, null, 0, "This is not a Classroom Manager licence file.");
            terms = LicenceTerms.FromJson(body);
            var keyId = file["keyId"]?.GetValue<string>() ?? "";
            if (!trustedKeys.TryGetValue(keyId, out var publicKey))
                return new LicenceCheck(LicenceState.Invalid, terms, 0, "The licence is signed with a key this program does not know.") { KeyUnknown = true };
            var signature = Convert.FromBase64String(file["signature"]?.GetValue<string>() ?? "");
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            // The terms are signed in their fixed form, so the file's own spacing or order does not matter, but every value does.
            if (!ecdsa.VerifyData(terms.Canonical(), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return new LicenceCheck(LicenceState.Invalid, terms, 0, "The licence was changed after it was signed.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or CryptographicException)
        {
            return new LicenceCheck(LicenceState.Invalid, null, 0, "The licence file is damaged.");
        }

        if (computerId is not null && terms.HostId.Length > 0 && !ComputerId.Same(terms.HostId, computerId))
            return new LicenceCheck(LicenceState.Invalid, terms, 0, "This licence was issued for another computer.") { WrongComputer = true };

        var daysLeft = terms.Ends.DayNumber - today.DayNumber;
        var state = daysLeft < 0 ? LicenceState.Expired : daysLeft <= RenewalNoticeDays ? LicenceState.EndingSoon : LicenceState.Active;
        return new LicenceCheck(state, terms, Math.Max(daysLeft, 0), "");
    }

    /// <summary>The public keys this build trusts for licences, by id. Filled from <see cref="LicenceKeys.Trusted"/>.</summary>
    public static IReadOnlyDictionary<string, byte[]> TrustedKeys() =>
        LicenceKeys.Trusted.Select(Convert.FromBase64String).ToDictionary(KeyId, key => key, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The public licence keys of Dausuva MB. The private key stays in Classroom Desk (Settings > Licence key shows the line to put here).
/// Keep an old key listed while licences signed with it are still in use.
/// </summary>
public static class LicenceKeys
{
    /// <summary>Base64 SubjectPublicKeyInfo of each trusted key, one per line, with its id (the id is not read). Add a line when the desk's key is replaced.</summary>
    public static readonly string[] Trusted =
    [
        // B560799FCE74851B: the desk's licence key, created 2026-10-25
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEMVHFGdSk0LN792Hu4plB1/5rnpSBpw9caxNZyKQB8aGRYes3lEJsRZ2e7LpCxRIEshbsp/fYui5Sr+yT6tYAFA==",
    ];
}
