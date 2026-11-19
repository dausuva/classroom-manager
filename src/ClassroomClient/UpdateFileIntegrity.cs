// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ClassroomManager.Client;

/// <summary>Mandatory content verification before any update executable is run.</summary>
static class UpdateFileIntegrity
{
    internal const string InvalidHash = "self.update requires a SHA-256 content hash containing exactly 64 hexadecimal characters. Re-upload using an up-to-date Host.";
    internal const string Mismatch = "Checksum mismatch - the uploaded file is incomplete or was altered. Re-upload and try again.";

    internal static bool TryReadHash(JsonNode? value, out string hash)
    {
        hash = "";
        if (value is not JsonValue json || !json.TryGetValue<string>(out var text) || !IsValidHash(text)) return false;
        hash = text!.ToLowerInvariant();
        return true;
    }

    static bool IsValidHash(string? hash) => hash is { Length: 64 } &&
        hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>The SHA-256 of a file, lower-case hex. The installed program is read with sharing allowed, because it is running.</summary>
    internal static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    internal static async Task<string?> VerifyAsync(string path, string expectedHash, CancellationToken ct)
    {
        // Keep this boundary fail-closed even if another caller is added later.
        if (!IsValidHash(expectedHash)) return InvalidHash;
        await using var stream = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(actual).Equals(expectedHash, StringComparison.OrdinalIgnoreCase) ? null : Mismatch;
    }
}
