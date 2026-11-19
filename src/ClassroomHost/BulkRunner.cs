// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>What <see cref="BulkRunner"/> tells its caller about each computer, in the order shown.</summary>
public sealed class BulkRunCallbacks
{
    /// <summary>The run was cancelled before this computer's turn; none of the other callbacks follow.</summary>
    public Action<ClientInfo>? Cancelled { get; init; }
    /// <summary>This computer's turn has come; a connection is about to be opened.</summary>
    public Action<ClientInfo>? Starting { get; init; }
    public Action<ClientInfo>? Succeeded { get; init; }
    /// <summary>Connecting or the action itself failed.</summary>
    public Action<ClientInfo, Exception>? Failed { get; init; }
    /// <summary>Always follows Starting, after Succeeded or Failed.</summary>
    public Action<ClientInfo>? Finished { get; init; }
}

/// <summary>
/// Runs one action against several computers, a limited number at a time, each over its own
/// authenticated connection. It knows nothing about windows, logs or output formats: the main
/// window and ClassroomCli both use it and add those through the callbacks, so a bulk action
/// behaves the same whichever of them started it.
/// </summary>
public static class BulkRunner
{
    public static async Task RunAsync(
        IReadOnlyList<ClientInfo> targets, HostSettings settings, Func<ClientInfo, ClientSession, Task> action,
        int maxParallel, CancellationToken cancellation, BulkRunCallbacks callbacks, bool allowExclusiveOperation = false)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));
        await Task.WhenAll(targets.Select(async client =>
        {
            try { await gate.WaitAsync(cancellation); }
            catch (OperationCanceledException)
            {
                callbacks.Cancelled?.Invoke(client);
                return;
            }
            callbacks.Starting?.Invoke(client);
            try
            {
                using var session = await ClientSession.OpenAsync(client, settings, ct: cancellation, allowExclusiveOperation: allowExclusiveOperation);
                await action(client, session);
                callbacks.Succeeded?.Invoke(client);
            }
            catch (Exception ex) { callbacks.Failed?.Invoke(client, ex); }
            finally
            {
                gate.Release();
                callbacks.Finished?.Invoke(client);
            }
        }));
    }
}
