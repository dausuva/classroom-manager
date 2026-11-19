// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>Core-owned adapters; applying a move and its recovery must not be interrupted halfway by window closure.</summary>
public interface IAddressFollowRuntime<TTarget, TAnswer>
{
    bool CanSearch { get; }
    IReadOnlyList<TTarget> CaptureMissing();
    Task<IReadOnlyList<TAnswer>> DiscoverAsync(CancellationToken cancellation);
    IReadOnlyList<TTarget> Apply(IReadOnlyList<TTarget> missing, IReadOnlyList<TAnswer> answers);
    void Save();
    bool CanRefresh(TTarget target);
    Task RefreshAsync(TTarget target);
    void RefreshFailed(TTarget target, Exception error);
    void Completed();
    void Failed(Exception error);
}

/// <summary>Window-scoped scan admission and address-follow sequencing. No transport, settings or presentation dependencies.</summary>
public sealed class DiscoveryWorkCoordinator(TimeSpan followInterval, TimeProvider? time = null) : IDisposable
{
    readonly object _gate = new();
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly TimeSpan _interval = followInterval > TimeSpan.Zero ? followInterval : throw new ArgumentOutOfRangeException(nameof(followInterval));
    readonly StatusRefreshWorkflow _refresh = new();
    long? _lastSearch;
    bool _busy, _closed;

    public bool IsBusy { get { lock (_gate) return _busy; } }
    bool IsClosed { get { lock (_gate) return _closed; } }

    /// <summary>Manual scans hold this lease through enrolment/persistence, not just the UDP receive window.</summary>
    public IDisposable? TryBeginScan()
    {
        lock (_gate)
        {
            if (_busy || _closed) return null;
            _busy = true;
            return new Lease(this);
        }
    }

    sealed class Lease(DiscoveryWorkCoordinator owner) : IDisposable
    {
        DiscoveryWorkCoordinator? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } owner) return;
            lock (owner._gate) owner._busy = false;
        }
    }

    public async Task<bool> FollowAsync<TTarget, TAnswer>(IAddressFollowRuntime<TTarget, TAnswer> runtime,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var lease = TryBeginScan();
        if (lease is null) return false;
        try
        {
            if (!runtime.CanSearch || (_lastSearch is { } last && _time.GetElapsedTime(last) < _interval)) return false;
            var missing = runtime.CaptureMissing().ToArray();
            if (missing.Length == 0) return false;
            _lastSearch = _time.GetTimestamp(); // Failed/cancelled searches still consume their broadcast interval.
            var answers = await runtime.DiscoverAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (IsClosed || !runtime.CanSearch) return true;
            var moved = runtime.Apply(missing, answers).ToArray();
            if (moved.Length == 0) return true;

            // From here on, finish accepted state/recovery as before. Cancellation is only safe before applying moves.
            runtime.Save();
            await _refresh.RunAsync(() => moved, runtime.CanRefresh, (target, _) => runtime.RefreshAsync(target),
                runtime.RefreshFailed, () => { runtime.Save(); runtime.Completed(); });
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) { runtime.Failed(error); return true; }
    }

    // The existing window-work lifetime cancels observational discovery. Accepted recovery is allowed to drain.
    public void Dispose() { lock (_gate) _closed = true; }
}
