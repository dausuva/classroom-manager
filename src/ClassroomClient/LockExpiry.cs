// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;

namespace ClassroomManager.Client;

/// <summary>
/// How long a lock lasts when the Host does not say. A lock that nobody releases (a Host that crashed, a teacher who left, a computer whose Host key changed) must not hold a class
/// for ever, so the service ends every lock itself after this time; a Host may ask for another length with a "minutes" argument (1 to 720). The service does this, not the helper,
/// so it also holds for a helper that restarts, and for every signed-in account.
/// </summary>
static class LockPolicy
{
    public const int DefaultMinutes = 30;
    public const int MaxMinutes = 720;

    public static int Minutes(JsonObject? args)
    {
        var asked = args?["minutes"] is JsonValue value && value.TryGetValue<int>(out var minutes) ? minutes : DefaultMinutes;
        return Math.Clamp(asked, 1, MaxMinutes);
    }
}

/// <summary>One pending automatic end of a lock. Starting a new one replaces the old one; <see cref="Cancel"/> stops it.</summary>
sealed class LockExpiry
{
    CancellationTokenSource? _cts;
    readonly object _gate = new();

    public void Start(TimeSpan after, Func<Task> expired)
    {
        CancellationToken token;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(after, token); }
            catch (OperationCanceledException) { return; }
            if (!token.IsCancellationRequested) await expired();
        });
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }
}
