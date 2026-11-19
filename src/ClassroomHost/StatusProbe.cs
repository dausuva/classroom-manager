// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>Allowlisted, copied status data; no keys, generic command API or mutable wire object.</summary>
public sealed record StatusPing
{
    public string? MachineName { get; init; }
    public string UserName { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string Mac { get; init; } = "";
    public string Version { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string WindowsVersion { get; init; } = "";
    public int? BlockedWebsiteCount { get; init; }
    public IReadOnlyList<string>? Capabilities { get; init; }
    public bool UserAgent { get; init; }
    public string InterfaceLanguage { get; init; } = "";
    public long? UptimeSeconds { get; init; }
    public long? UtcTicks { get; init; }
    public string Role { get; init; } = "";
    public string? DesktopRuntime { get; init; }
}

public sealed record StatusWindows(int Applications, int Windows);
public sealed record StatusObservation(StatusPing Ping, DateTime SentUtc, double RoundTripMilliseconds);

/// <summary>Read-only view of an already authenticated, caller-owned connection.</summary>
public interface IStatusConnection
{
    Task<StatusPing> PingAsync(CancellationToken cancellation);
    Task<string?> InterfaceLanguageAsync(CancellationToken cancellation);
    Task<StatusWindows> OpenWindowsAsync(CancellationToken cancellation);
}

/// <summary>Core read orchestration only. Identity checks, policy recovery and presentation remain with the caller.</summary>
public sealed class StatusProbe
{
    readonly TimeProvider _time;
    readonly TimeSpan _pingTimeout, _optionalTimeout;
    public StatusProbe(TimeProvider? time = null, TimeSpan? pingTimeout = null, TimeSpan? optionalTimeout = null)
    {
        _time = time ?? TimeProvider.System;
        _pingTimeout = pingTimeout ?? TimeSpan.FromSeconds(8);
        _optionalTimeout = optionalTimeout ?? TimeSpan.FromSeconds(5);
        if (_pingTimeout <= TimeSpan.Zero || _optionalTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException();
    }

    public async Task<StatusObservation> PingAsync(IStatusConnection connection, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var sent = _time.GetUtcNow().UtcDateTime;
        var started = _time.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(_pingTimeout);
        try
        {
            var ping = await connection.PingAsync(timeout.Token);
            cancellation.ThrowIfCancellationRequested();
            return new(ping, sent, _time.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("The client stopped answering."); }
    }

    public Task<string?> InterfaceLanguageAsync(IStatusConnection connection, bool supported, bool hasUser,
        CancellationToken cancellation = default) => OptionalAsync(supported && hasUser, connection.InterfaceLanguageAsync, cancellation);

    // Preserve the existing Windows read even when the helper flag is absent: older Clients may still answer it.
    public Task<StatusWindows?> OpenWindowsAsync(IStatusConnection connection, bool supported,
        CancellationToken cancellation = default) => OptionalAsync(supported, connection.OpenWindowsAsync, cancellation);

    async Task<T?> OptionalAsync<T>(bool allowed, Func<CancellationToken, Task<T>> read, CancellationToken cancellation) where T : class?
    {
        cancellation.ThrowIfCancellationRequested();
        if (!allowed) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(_optionalTimeout);
        try
        {
            var result = await read(timeout.Token);
            cancellation.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            cancellation.ThrowIfCancellationRequested();
            return null; // Optional metadata must not turn a successful ping into an offline computer.
        }
    }
}
