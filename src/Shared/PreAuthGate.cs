// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>
/// The limits applied to a connection before it has proven it knows a key (T-183). The defaults are generous on purpose:
/// a lesson with 30 computers, one teacher address and a Health run must never trip them.
/// </summary>
public sealed record ConnectionLimits
{
    /// <summary>Connections allowed to be in TLS or handshake at the same time, all sources together.</summary>
    public int MaxPending { get; init; } = 64;
    /// <summary>The same, for one source address.</summary>
    public int MaxPendingPerSource { get; init; } = 8;
    /// <summary>New connections one source may open at once before it must wait (token bucket size).</summary>
    public int BurstPerSource { get; init; } = 40;
    /// <summary>New connections per second one source earns back.</summary>
    public double RefillPerSecond { get; init; } = 5;
    /// <summary>Failed handshakes from one source that are forgiven before back-off starts.</summary>
    public int FailuresBeforeBackoff { get; init; } = 5;
    /// <summary>The first wait after the failures above; it doubles with every further failure.</summary>
    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>A source that has been quiet this long starts again from zero failures.</summary>
    public TimeSpan FailureMemory { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>How long a connection may take to finish TLS and the key proof.</summary>
    public TimeSpan SetupDeadline { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Authenticated sessions one key may hold open on one Client.</summary>
    public int MaxSessionsPerKey { get; init; } = 32;
    /// <summary>Requests one authenticated connection may send in a burst, and per second after that.</summary>
    public int RequestBurst { get; init; } = 1000;
    public double RequestsPerSecond { get; init; } = 500;
    /// <summary>Sources remembered at most; idle ones are dropped first.</summary>
    public int MaxSources { get; init; } = 4096;
}

/// <summary>Why a new connection was or was not let in.</summary>
public enum Admission { Admitted, TooManyPending, TooManyFromSource, RateLimited, BackedOff }

/// <summary>
/// Decides, from the source address alone and before any TLS or key work, whether a new connection may start its handshake:
/// a cap on connections in setup (all together and per source), a token bucket per source, and exponential back-off after
/// failed handshakes. It keeps counts and times, never anything a peer sent. Thread-safe.
/// </summary>
public sealed class PreAuthGate
{
    sealed class Source
    {
        public int Pending;
        public double Tokens;
        public DateTimeOffset Refilled;
        public int Failures;
        public DateTimeOffset LastFailure;
        public DateTimeOffset BlockedUntil;
        public DateTimeOffset LastSeen;
    }

    readonly ConnectionLimits _limits;
    readonly TimeProvider _time;
    readonly object _lock = new();
    readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    int _pending;
    readonly long[] _counts = new long[Enum.GetValues<Admission>().Length];

    public PreAuthGate(ConnectionLimits? limits = null, TimeProvider? time = null)
    {
        _limits = limits ?? new ConnectionLimits();
        _time = time ?? TimeProvider.System;
    }

    public ConnectionLimits Limits => _limits;
    public int Pending { get { lock (_lock) return _pending; } }

    /// <summary>Asks to start a handshake. After <see cref="Admission.Admitted"/> the caller must call <see cref="Leave"/> exactly once.</summary>
    public Admission TryEnter(string source)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var state = Find(source, now);
            Admission result;
            if (state.BlockedUntil > now) result = Admission.BackedOff;
            else if (state.Pending >= _limits.MaxPendingPerSource) result = Admission.TooManyFromSource;
            else if (_pending >= _limits.MaxPending) result = Admission.TooManyPending;
            else
            {
                Refill(state, now);
                if (state.Tokens < 1) result = Admission.RateLimited;
                else
                {
                    state.Tokens -= 1;
                    state.Pending++;
                    _pending++;
                    result = Admission.Admitted;
                }
            }
            _counts[(int)result]++;
            return result;
        }
    }

    /// <summary>The handshake of an admitted connection ended, in either way.</summary>
    public void Leave(string source)
    {
        lock (_lock)
        {
            if (_pending > 0) _pending--;
            if (_sources.TryGetValue(source, out var state) && state.Pending > 0) state.Pending--;
        }
    }

    /// <summary>The handshake failed: wrong key, no proof in time, broken TLS. Repeated failures make the source wait.</summary>
    public void Failed(string source)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var state = Find(source, now);
            if (now - state.LastFailure > _limits.FailureMemory) state.Failures = 0;
            state.Failures++;
            state.LastFailure = now;
            var over = state.Failures - _limits.FailuresBeforeBackoff - 1;   // the first N failures are forgiven
            if (over >= 0)
            {
                var seconds = _limits.FirstBackoff.TotalSeconds * Math.Pow(2, Math.Min(over, 30));
                state.BlockedUntil = now + TimeSpan.FromSeconds(Math.Min(seconds, _limits.MaxBackoff.TotalSeconds));
            }
        }
    }

    /// <summary>The source proved a key: its failures are forgiven.</summary>
    public void Succeeded(string source)
    {
        lock (_lock)
        {
            if (!_sources.TryGetValue(source, out var state)) return;
            state.Failures = 0;
            state.BlockedUntil = default;
        }
    }

    /// <summary>How many connections were refused for each reason since the last call; the counters start again.</summary>
    public Dictionary<Admission, long> TakeCounts()
    {
        lock (_lock)
        {
            var taken = new Dictionary<Admission, long>();
            foreach (var reason in Enum.GetValues<Admission>())
            {
                if (reason != Admission.Admitted && _counts[(int)reason] > 0) taken[reason] = _counts[(int)reason];
                _counts[(int)reason] = 0;
            }
            return taken;
        }
    }

    Source Find(string key, DateTimeOffset now)
    {
        if (!_sources.TryGetValue(key, out var state))
        {
            if (_sources.Count >= _limits.MaxSources) Prune(now);
            state = new Source { Tokens = _limits.BurstPerSource, Refilled = now };
            // When the table is still full of busy sources the newcomer is judged by the global caps alone and not remembered.
            if (_sources.Count < _limits.MaxSources) _sources[key] = state;
        }
        state.LastSeen = now;
        return state;
    }

    void Refill(Source state, DateTimeOffset now)
    {
        var earned = (now - state.Refilled).TotalSeconds * _limits.RefillPerSecond;
        if (earned > 0)
        {
            state.Tokens = Math.Min(_limits.BurstPerSource, state.Tokens + earned);
            state.Refilled = now;
        }
    }

    void Prune(DateTimeOffset now)
    {
        foreach (var key in _sources.Where(pair => pair.Value.Pending == 0 && pair.Value.BlockedUntil <= now
                                                   && now - pair.Value.LastSeen > TimeSpan.FromMinutes(1)).Select(pair => pair.Key).ToList())
            _sources.Remove(key);
    }
}
