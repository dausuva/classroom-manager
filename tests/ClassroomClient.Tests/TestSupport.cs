// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomClient.Tests;

/// <summary>
/// Waits for something the server does on its own thread, instead of sleeping for a guessed time. A fixed sleep fails on a slow or busy
/// machine (a CI runner) and wastes time on a fast one; this returns as soon as the condition holds and fails with its name when it never does.
/// </summary>
static class Eventually
{
    public static async Task WaitAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"Timed out waiting until {what}.");
            await Task.Delay(20);
        }
    }

    /// <summary>Retries an action that is expected to start working (a freed slot, a finished server step) until it does.</summary>
    public static async Task<T> SucceedsAsync<T>(Func<Task<T>> action, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            try { return await action(); }
            catch (Exception) when (DateTime.UtcNow < deadline) { await Task.Delay(50); }
            catch (Exception ex) { throw new Xunit.Sdk.XunitException($"Timed out waiting until {what}: {ex.Message}"); }
        }
    }
}

/// <summary>A clock that only moves when the test says so, for limits that are measured in time.</summary>
sealed class TestClock : TimeProvider
{
    DateTimeOffset _now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
