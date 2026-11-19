// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;

namespace ClassroomManager.Shared;

/// <summary>
/// How often a Client answers the UDP discovery question (T-184). The question is unauthenticated and its source address can
/// be forged, so the answer is rationed instead of given to everyone who asks.
/// </summary>
public sealed record DiscoveryLimits
{
    /// <summary>Answers one source address gets per second and per minute.</summary>
    public int PerSourcePerSecond { get; init; } = 3;
    public int PerSourcePerMinute { get; init; } = 30;
    /// <summary>Answers all sources together get per second.</summary>
    public int GlobalPerSecond { get; init; } = 100;
    /// <summary>Answer only sources on one of this computer's own subnets. Off by default: a lab with several subnets would stop finding computers.</summary>
    public bool LocalSubnetOnly { get; init; }
    /// <summary>Sources remembered at most.</summary>
    public int MaxSources { get; init; } = 2048;
}

/// <summary>Decides whether a discovery question gets an answer. Counts and times only; thread-safe.</summary>
public sealed class DiscoveryLimiter
{
    readonly DiscoveryLimits _limits;
    readonly TimeProvider _time;
    readonly object _lock = new();
    readonly Dictionary<string, Queue<DateTimeOffset>> _answers = new(StringComparer.Ordinal);
    readonly Queue<DateTimeOffset> _all = new();
    long _dropped;

    public DiscoveryLimiter(DiscoveryLimits? limits = null, TimeProvider? time = null)
    {
        _limits = limits ?? new DiscoveryLimits();
        _time = time ?? TimeProvider.System;
    }

    public DiscoveryLimits Limits => _limits;

    /// <summary>True when the question may be answered; the answer is then counted against the limits.</summary>
    public bool TryAnswer(string source)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            Trim(_all, now - TimeSpan.FromSeconds(1));
            if (_all.Count >= _limits.GlobalPerSecond) { _dropped++; return false; }

            if (!_answers.TryGetValue(source, out var recent))
            {
                if (_answers.Count >= _limits.MaxSources) Prune(now);
                if (_answers.Count >= _limits.MaxSources) { _dropped++; return false; }
                _answers[source] = recent = new Queue<DateTimeOffset>();
            }
            Trim(recent, now - TimeSpan.FromMinutes(1));
            var lastSecond = recent.Count(time => time > now - TimeSpan.FromSeconds(1));
            if (recent.Count >= _limits.PerSourcePerMinute || lastSecond >= _limits.PerSourcePerSecond) { _dropped++; return false; }

            recent.Enqueue(now);
            _all.Enqueue(now);
            return true;
        }
    }

    /// <summary>How many questions were left unanswered since the last call.</summary>
    public long TakeDropped()
    {
        lock (_lock) { var taken = _dropped; _dropped = 0; return taken; }
    }

    static void Trim(Queue<DateTimeOffset> queue, DateTimeOffset before)
    {
        while (queue.Count > 0 && queue.Peek() <= before) queue.Dequeue();
    }

    void Prune(DateTimeOffset now)
    {
        foreach (var key in _answers.Where(pair => { Trim(pair.Value, now - TimeSpan.FromMinutes(1)); return pair.Value.Count == 0; })
                     .Select(pair => pair.Key).ToList())
            _answers.Remove(key);
    }

    /// <summary>Whether two IPv4 addresses are on one network with this prefix length (for example 24 for 255.255.255.0).</summary>
    public static bool SameSubnet(IPAddress a, IPAddress b, int prefixLength)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (b.IsIPv4MappedToIPv6) b = b.MapToIPv4();
        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || b.AddressFamily != a.AddressFamily) return false;
        if (prefixLength is < 0 or > 32) return false;
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var ax = (uint)(x[0] << 24 | x[1] << 16 | x[2] << 8 | x[3]);
        var by = (uint)(y[0] << 24 | y[1] << 16 | y[2] << 8 | y[3]);
        return (ax & mask) == (by & mask);
    }
}
