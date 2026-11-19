// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.Cryptography;
using System.Text;

namespace ClassroomManager.Shared;

/// <summary>Rules and helpers for the classroom shared key (used by host, client and setup).</summary>
public static class SharedKey
{
    public const int MinLength = 12;
    public const int MaxLength = 256;

    /// <summary>Lower-case letters and digits without look-alikes (0/o, 1/l/i), so a key can be read aloud or retyped.</summary>
    public const string Alphabet = "23456789abcdefghjkmnpqrstuvwxyz";

    /// <summary>
    /// A random key like "k7mq2-xh9vd-3ptwa-e8fzn-4bjrs": 25 characters from a 31-symbol alphabet
    /// (about 124 bits), in dash-separated groups so it is easy to copy or type.
    /// </summary>
    public static string Generate(int groups = 5, int groupLength = 5)
    {
        if (groups < 1 || groupLength < 1) throw new ArgumentOutOfRangeException(nameof(groups));
        var sb = new StringBuilder(groups * (groupLength + 1));
        for (int g = 0; g < groups; g++)
        {
            if (g > 0) sb.Append('-');
            for (int i = 0; i < groupLength; i++)
                sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }
        return sb.ToString();
    }

    /// <summary>Checks a proposed key; on failure <paramref name="reason"/> says why in plain words.</summary>
    public static bool IsAcceptable(string? key, out string reason)
    {
        if (string.IsNullOrWhiteSpace(key)) { reason = "The key is empty."; return false; }
        if (key != key.Trim()) { reason = "The key must not start or end with spaces."; return false; }
        if (key.Length < MinLength) { reason = $"The key must be at least {MinLength} characters."; return false; }
        if (key.Length > MaxLength) { reason = $"The key must be at most {MaxLength} characters."; return false; }
        if (key.Any(char.IsControl)) { reason = "The key must not contain control characters."; return false; }
        reason = "";
        return true;
    }

    /// <summary>
    /// A short, non-reversible label for a key (8 hex characters), so the host can record which key
    /// a computer uses and logs can mention a key without revealing it.
    /// </summary>
    public static string Fingerprint(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("classroom-manager-key:" + key));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
