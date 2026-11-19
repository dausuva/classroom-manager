// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>Core, window-scoped batch admission and bounded work. Callbacks retain their caller's synchronization context.</summary>
public sealed class StatusRefreshWorkflow(int maxParallel = 16)
{
    readonly int _limit = maxParallel > 0 ? maxParallel : throw new ArgumentOutOfRangeException(nameof(maxParallel));
    int _running;
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public async Task<bool> RunAsync<T>(Func<IReadOnlyList<T>> capture, Func<T, bool> canStart,
        Func<T, CancellationToken, Task> refresh, Action<T, Exception> failed, Action completed,
        CancellationToken cancellation = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var targets = capture().ToArray();
            using var gate = new SemaphoreSlim(_limit);
            await Task.WhenAll(targets.Select(async target =>
            {
                await gate.WaitAsync(cancellation);
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (canStart(target)) await refresh(target, cancellation);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception error) { failed(target, error); }
                finally { gate.Release(); }
            }));
            cancellation.ThrowIfCancellationRequested();
            completed();
            return true;
        }
        finally { Volatile.Write(ref _running, 0); }
    }
}
