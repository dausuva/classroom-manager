// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace ClassroomManager.Shared;

/// <summary>Exact, independently enrolled leaf pins; never TOFU or a signing-certificate pin.</summary>
public static class TlsEndpointTrust
{
    public static bool IsFingerprint(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    public static RemoteCertificateValidationCallback Validator(IEnumerable<string> fingerprints)
    {
        // Snapshot once: a settings/UI mutation cannot change the policy mid-handshake.
        var pins = fingerprints.ToArray();
        if (pins.Length is < 1 or > 2 || pins.Any(pin => !IsFingerprint(pin)))
            throw new AuthenticationException("TLS endpoint trust is missing or invalid. Independently verify and enroll this computer's certificate SHA-256 before connecting. Do not turn TLS off.");
        var approved = pins.Select(Convert.FromHexString).ToArray();
        return (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            var actual = SHA256.HashData(certificate.GetRawCertData());
            bool matches = false;
            foreach (var pin in approved) matches |= CryptographicOperations.FixedTimeEquals(pin, actual);
            return matches;
        };
    }
}
