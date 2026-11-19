// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>A token bucket for the requests of one authenticated connection. Not thread-safe: one connection reads one request at a time.</summary>
public sealed class RequestBucket
{
    readonly int _burst;
    readonly double _perSecond;
    readonly TimeProvider _time;
    double _tokens;
    DateTimeOffset _refilled;

    public RequestBucket(int burst, double perSecond, TimeProvider? time = null)
    {
        _burst = burst;
        _perSecond = perSecond;
        _time = time ?? TimeProvider.System;
        _tokens = burst;
        _refilled = _time.GetUtcNow();
    }

    /// <summary>Takes one request's worth; false when the connection is asking faster than the limit allows.</summary>
    public bool TryTake()
    {
        var now = _time.GetUtcNow();
        _tokens = Math.Min(_burst, _tokens + (now - _refilled).TotalSeconds * _perSecond);
        _refilled = now;
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }
}

/// <summary>Counts the authenticated sessions per key, so one leaked key cannot hold every slot of a Client. Thread-safe.</summary>
public sealed class SessionCounter
{
    readonly object _lock = new();
    readonly Dictionary<string, int> _open = new(StringComparer.Ordinal);

    /// <summary>True and counted when the key has fewer than <paramref name="max"/> sessions open; the caller then calls <see cref="Close"/> once.</summary>
    public bool TryOpen(string key, int max)
    {
        lock (_lock)
        {
            _open.TryGetValue(key, out var count);
            if (count >= max) return false;
            _open[key] = count + 1;
            return true;
        }
    }

    public void Close(string key)
    {
        lock (_lock)
        {
            if (!_open.TryGetValue(key, out var count)) return;
            if (count <= 1) _open.Remove(key); else _open[key] = count - 1;
        }
    }

    public int Open(string key) { lock (_lock) return _open.TryGetValue(key, out var count) ? count : 0; }
}
