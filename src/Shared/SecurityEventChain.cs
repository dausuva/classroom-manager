// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassroomManager.Shared;

/// <summary>The outcome of checking a security event log: whether the chain is unbroken and, if not, where it breaks.</summary>
public sealed record ChainResult(bool Ok, int Count, int BadLine, string Reason, long LastSeq, string LastHash);

/// <summary>
/// The format of the security event log (T-191): one JSON object per line, each carrying the number of the event, its time, its kind,
/// a few text fields, the hash of the previous line and its own hash. Because every line contains the hash of the one before, a line that is
/// changed, removed from the middle or inserted shows up as the first line whose hash or link does not fit. Removing the *last* lines cannot be
/// seen from the file alone; the Client therefore reports its last number and hash with every Ping, and the Host can compare.
/// The hash is a tamper-evidence aid against accidents and casual edits, not against an administrator who rewrites the whole chain.
/// A line never contains a key: <see cref="Line"/> takes plain text fields chosen by the caller and the callers pass fingerprints and counts only.
/// </summary>
public static class SecurityEventChain
{
    public const string Genesis = "0000000000000000";

    static string Hash(string prev, long seq, string time, string kind, IReadOnlyDictionary<string, string> data)
    {
        var canonical = JsonSerializer.Serialize(new object[] { seq, time, kind, data.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new[] { pair.Key, pair.Value }).ToArray() });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prev + "|" + canonical)))[..16].ToLowerInvariant();
    }

    public static string Time(DateTimeOffset when) => when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>One log line (without the line break) and its hash, which is the <c>prev</c> of the next line.</summary>
    public static (string Text, string Hash) Line(long seq, DateTimeOffset when, string kind, IReadOnlyDictionary<string, string> data, string prev)
    {
        var time = Time(when);
        var hash = Hash(prev, seq, time, kind, data);
        var text = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["seq"] = seq, ["t"] = time, ["kind"] = kind,
            ["data"] = data.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
            ["prev"] = prev, ["hash"] = hash,
        });
        return (text, hash);
    }

    /// <summary>Checks the lines in order. The first line sets the starting point (a rotated file starts in the middle of a chain); every later line must follow it.</summary>
    public static ChainResult Verify(IEnumerable<string> lines, string? expectedFirstPrev = null)
    {
        long lastSeq = 0;
        var lastHash = expectedFirstPrev ?? Genesis;
        var count = 0;
        var number = 0;
        foreach (var raw in lines)
        {
            number++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                var seq = root.GetProperty("seq").GetInt64();
                var time = root.GetProperty("t").GetString() ?? "";
                var kind = root.GetProperty("kind").GetString() ?? "";
                var prev = root.GetProperty("prev").GetString() ?? "";
                var hash = root.GetProperty("hash").GetString() ?? "";
                var data = root.GetProperty("data").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? "");
                if (count > 0 && seq != lastSeq + 1) return new ChainResult(false, count, number, $"event {seq} follows event {lastSeq}: lines are missing or out of order", lastSeq, lastHash);
                if ((count > 0 || expectedFirstPrev is not null) && prev != lastHash) return new ChainResult(false, count, number, "the link to the previous line does not fit", lastSeq, lastHash);
                if (Hash(prev, seq, time, kind, data) != hash) return new ChainResult(false, count, number, "the line was changed after it was written", lastSeq, lastHash);
                lastSeq = seq;
                lastHash = hash;
                count++;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                return new ChainResult(false, count, number, "the line is not a valid event", lastSeq, lastHash);
            }
        }
        return new ChainResult(true, count, 0, "", lastSeq, lastHash);
    }
}
