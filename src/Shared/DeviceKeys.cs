// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.Cryptography;
using System.Text;

namespace ClassroomManager.Shared;

/// <summary>
/// One key per computer (T-188). The Host keeps one lab master key and derives each computer's key from it:
/// <c>HKDF-SHA256(master, salt = "device-v1", info = role | clientId)</c>. A Client stores only its own derived key; HKDF cannot be run
/// backwards, so a stolen <c>client.json</c> reveals neither the master nor the key of any other computer, and a key derived for one
/// computer is refused by the next. The role is part of the derivation, so a key made for one role cannot be turned into another's.
/// The Client needs no change: it simply holds a key, and the Host gives it this one with the ordinary key change.
/// </summary>
public static class DeviceKeys
{
    /// <summary>Marks a derived key at a glance. Typed keys never start with it.</summary>
    public const string Prefix = "dk1-";

    static readonly byte[] Salt = "device-v1"u8.ToArray();

    /// <summary>The key of the computer with this client id under this master key, as text that passes <see cref="SharedKey.IsAcceptable"/>.</summary>
    public static string Derive(string masterKey, string clientId, string role = "admin")
    {
        if (string.IsNullOrWhiteSpace(masterKey)) throw new ArgumentException("The master key is empty.", nameof(masterKey));
        var id = Normalize(clientId);
        if (id.Length == 0) throw new ArgumentException("A derived key needs the client id of the computer.", nameof(clientId));
        var info = Encoding.UTF8.GetBytes(role.ToLowerInvariant() + "|" + id);
        var bytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(masterKey), 32, Salt, info);
        return Prefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool IsDerived(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);

    static string Normalize(string clientId) => (clientId ?? "").Trim().ToLowerInvariant();
}
