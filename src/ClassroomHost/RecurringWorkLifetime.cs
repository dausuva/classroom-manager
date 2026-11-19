// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// Owns recurring registrations on one caller-owned thread. Disposing prevents new ticks,
/// including callbacks already queued by the timer; it does not cancel work already dispatched.
/// This is an internal application boundary, not an extension SDK contract.
/// </summary>
public sealed class RecurringWorkLifetime(Func<TimeSpan, Action, IDisposable> startTimer) : IDisposable
{
    readonly Func<TimeSpan, Action, IDisposable> _startTimer = startTimer ?? throw new ArgumentNullException(nameof(startTimer));
    readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    bool _disposed;

    /// <summary>Starts a named task once. A closed owner or an existing name returns false.</summary>
    public bool Start(string name, TimeSpan interval, Action tick)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(tick);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (_disposed || _registrations.ContainsKey(name)) return false;

        var registration = new Registration(() => { if (!_disposed) tick(); });
        _registrations.Add(name, registration);
        try
        {
            registration.Attach(_startTimer(interval, registration.Tick)
                ?? throw new InvalidOperationException());
            return !_disposed;
        }
        catch
        {
            _registrations.Remove(name);
            registration.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var registrations = _registrations.Values.ToArray();
        _registrations.Clear();
        // Stop every source even if a supplied adapter fails during cleanup.
        List<Exception>? failures = null;
        foreach (var registration in registrations)
        {
            try { registration.Dispose(); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
        }
        if (failures is not null) throw new AggregateException(failures);
    }

    sealed class Registration(Action tick) : IDisposable
    {
        Action? _tick = tick;
        IDisposable? _timer;

        public void Tick() => _tick?.Invoke();

        public void Attach(IDisposable timer)
        {
            // A factory can pump the owning thread and cause closure before it returns.
            if (_tick is null) timer.Dispose();
            else _timer = timer;
        }

        public void Dispose()
        {
            _tick = null;
            var timer = _timer;
            _timer = null;
            timer?.Dispose();
        }
    }
}
