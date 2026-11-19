// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// A Client blocks a source address for up to five minutes after a few refused keys, and every try during that time
/// (even with the right key) is dropped and renews the block. After every key had been refused by a computer the Host
/// therefore stops connecting to it for a while, so the 30-second status check cannot keep the block alive, and says why.
/// </summary>
public static class KeyRefusalPause
{
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(5);

    /// <summary>Start of the message of a try that was not made because of a pause; the window shows its own status for it.</summary>
    public const string MessageStart = "Not tried: this computer refused the keys of this PC a moment ago";

    static readonly ConcurrentDictionary<string, DateTime> Until = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For tests: the time the pause is measured against.</summary>
    public static Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    static string Key(ClientInfo client) => client.Address + ":" + client.Port;

    public static void Start(ClientInfo client) => Until[Key(client)] = Now() + Length;

    public static void Clear(ClientInfo client) => Until.TryRemove(Key(client), out _);

    /// <summary>When the pause for this computer ends, or null when there is none.</summary>
    public static DateTime? EndsAt(ClientInfo client)
    {
        if (!Until.TryGetValue(Key(client), out var end)) return null;
        if (end > Now()) return end;
        Until.TryRemove(Key(client), out _);
        return null;
    }

    public static bool IsPauseMessage(string? message) =>
        message is not null && message.StartsWith(MessageStart, StringComparison.Ordinal);

    public static AuthException NotTried(DateTime endsAtUtc)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((endsAtUtc - Now()).TotalMinutes));
        return new AuthException($"Not tried: this computer refused the keys of this PC a moment ago. It may ignore this PC for about {minutes} more minute(s); nothing needs to be done.");
    }

    /// <summary>Forgets every pause (the keys of this PC were changed, so an earlier refusal says nothing now).</summary>
    public static void ClearAll() => Until.Clear();
}
