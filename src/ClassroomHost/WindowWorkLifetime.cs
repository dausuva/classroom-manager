// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// Owns cancellation for observational work whose results belong only to one window. Closing the
/// owner cancels active work and makes later starts no-ops; cancellation caused by closure is normal.
/// </summary>
public sealed class WindowWorkLifetime : IDisposable
{
    readonly object _gate = new();
    readonly CancellationTokenSource _shutdown = new();
    int _active;
    bool _disposed;
    bool _cancellationFinished;

    public bool IsDisposed => Volatile.Read(ref _disposed);

    public int ActiveCount
    {
        get { lock (_gate) return _active; }
    }

    public Task RunAsync(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            _active++;
            token = _shutdown.Token;
        }
        return RunCoreAsync(work, token);
    }

    async Task RunCoreAsync(Func<CancellationToken, Task> work, CancellationToken token)
    {
        try
        {
            await work(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Window closure is the successful terminal state for work owned only by that window.
        }
        finally
        {
            var release = false;
            lock (_gate)
            {
                _active--;
                release = _disposed && _cancellationFinished && _active == 0;
            }
            if (release) _shutdown.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
        }

        try
        {
            _shutdown.Cancel();
        }
        finally
        {
            var release = false;
            lock (_gate)
            {
                _cancellationFinished = true;
                release = _active == 0;
            }
            if (release) _shutdown.Dispose();
        }
    }
}
