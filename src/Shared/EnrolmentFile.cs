// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassroomManager.Shared;

/// <summary>
/// What a student computer says about itself so a Host can approve its certificate without fetching it from the
/// network: written on the computer by Classroom Client Health (<c>--export-enrolment</c>), carried on a USB stick or
/// a share, read by the Host. The certificate fingerprint comes from the computer's own protected file. A tag made with the
/// lab key shows that whoever wrote the file holds the lab key; it does not make the file secret, and nobody who holds
/// the lab key needed a file to take control of the lab in the first place.
/// </summary>
public sealed class EnrolmentFile
{
    public const string FormatName = "classroom-enrolment-1";

    public string Format { get; set; } = FormatName;
    public string MachineName { get; set; } = "";
    public string ClientId { get; set; } = "";
    /// <summary>The hardware addresses of the computer's network cards, 12 capital hex digits each.</summary>
    public List<string> Cards { get; set; } = [];
    public string CertificateSha256 { get; set; } = "";
    /// <summary>UTC, ISO 8601.</summary>
    public string CreatedUtc { get; set; } = "";
    /// <summary>HMAC-SHA256 over the other fields with the lab key, as hex.</summary>
    public string Tag { get; set; } = "";

    static readonly JsonSerializerOptions Options = new(Protocol.Json) { WriteIndented = true };

    public static EnrolmentFile Create(string machineName, string clientId, IEnumerable<string> cards, string certificateSha256, DateTime nowUtc, string labKey)
    {
        var file = new EnrolmentFile
        {
            MachineName = machineName.Trim(),
            ClientId = clientId.Trim(),
            Cards = cards.Select(Normalize).Where(card => card.Length == 12).Distinct().Order(StringComparer.Ordinal).ToList(),
            CertificateSha256 = certificateSha256.Trim().ToUpperInvariant(),
            CreatedUtc = nowUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        file.Tag = file.ComputeTag(labKey);
        return file;
    }

    /// <summary>The bytes the tag covers: every field, in a fixed order, one per line, so no field can be moved into another.</summary>
    string Canonical() => string.Join("\n", "enrolment-v1", Format, MachineName, ClientId, string.Join(",", Cards), CertificateSha256, CreatedUtc);

    string ComputeTag(string labKey) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(labKey), Encoding.UTF8.GetBytes(Canonical())));

    /// <summary>Whether the tag was made with one of these keys. The comparison takes the same time whatever matches.</summary>
    public bool Verify(IEnumerable<string> labKeys)
    {
        if (Tag.Length != 64) return false;
        byte[] actual;
        try { actual = Convert.FromHexString(Tag); } catch (FormatException) { return false; }
        var matched = false;
        foreach (var key in labKeys.Where(key => !string.IsNullOrWhiteSpace(key)))
            matched |= CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(ComputeTag(key)));
        return matched;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads and checks the shape of a file; the tag is checked separately with <see cref="Verify"/>.</summary>
    public static bool TryParse(string json, out EnrolmentFile file, out string error)
    {
        file = new EnrolmentFile();
        try
        {
            var parsed = JsonSerializer.Deserialize<EnrolmentFile>(json, Protocol.Json);
            if (parsed is null) { error = "The file is empty."; return false; }
            file = parsed;
        }
        catch (JsonException ex) { error = "Not a valid enrolment file: " + ex.Message; return false; }
        if (file.Format != FormatName) { error = $"Unknown format '{file.Format}'."; return false; }
        file.Cards = file.Cards.Select(Normalize).Where(card => card.Length == 12).Distinct().Order(StringComparer.Ordinal).ToList();
        file.CertificateSha256 = file.CertificateSha256.Trim().ToUpperInvariant();
        if (!TlsEndpointTrust.IsFingerprint(file.CertificateSha256)) { error = "The certificate fingerprint is not 64 hex digits."; return false; }
        if (file.Cards.Count == 0 && file.ClientId.Length == 0) { error = "The file names neither a network card nor a client id."; return false; }
        if (!DateTime.TryParse(file.CreatedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out _))
        { error = "The creation time is not a date."; return false; }
        error = "";
        return true;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime Created => DateTime.Parse(CreatedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    static string Normalize(string? value)
    {
        var digits = new string((value ?? "").Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        return digits.Length == 12 ? digits : "";
    }
}
