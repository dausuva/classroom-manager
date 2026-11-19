// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

// T-222: typed status reads preserve optional-data failures and do not own authenticated sessions.
public sealed class StatusProbeTests
{
    sealed class Connection : IStatusConnection, IDisposable
    {
        public Func<CancellationToken, Task<StatusPing>> ReadPing { get; set; } = _ => Task.FromResult(new StatusPing());
        public Func<CancellationToken, Task<string?>> ReadLanguage { get; set; } = _ => Task.FromResult<string?>("lt-LT");
        public Func<CancellationToken, Task<StatusWindows>> ReadWindows { get; set; } = _ => Task.FromResult(new StatusWindows(2, 3));
        public int Pings, Languages, Windows;
        public bool Disposed;
        public Task<StatusPing> PingAsync(CancellationToken cancellation) { Pings++; return ReadPing(cancellation); }
        public Task<string?> InterfaceLanguageAsync(CancellationToken cancellation) { Languages++; return ReadLanguage(cancellation); }
        public Task<StatusWindows> OpenWindowsAsync(CancellationToken cancellation) { Windows++; return ReadWindows(cancellation); }
        public void Dispose() => Disposed = true;
    }

    sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task Ping_uses_injected_monotonic_timing_and_does_not_dispose_the_session()
    {
        var clock = new Clock();
        var data = new StatusPing { ClientId = "id", UserName = "Close" };
        var connection = new Connection { ReadPing = _ => { clock.Ticks += 125; return Task.FromResult(data); } };
        var result = await new StatusProbe(clock).PingAsync(connection);
        Assert.Same(data, result.Ping);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, result.SentUtc);
        Assert.Equal(125, result.RoundTripMilliseconds);
        Assert.False(connection.Disposed);
        connection.Dispose();
        Assert.True(connection.Disposed);
    }

    [Fact]
    public void Wire_adapter_copies_only_status_fields_and_keeps_missing_capabilities_distinct()
    {
        var data = new JsonObject
        {
            ["machineName"] = "Close", ["userName"] = "Close", ["clientId"] = " id ", ["mac"] = "01:02:03:04:05:06",
            ["version"] = "1.20", ["appVersion"] = "1.2.0.42", ["architecture"] = "x64", ["windowsVersion"] = "Windows 11",
            ["blockedWebsiteCount"] = 3, ["capabilities"] = new JsonArray(Cmd.ProcWindows, "", null), ["userAgent"] = true,
            ["interfaceLanguage"] = "en-US", ["uptimeSec"] = 500L, ["utcTicks"] = DateTime.UnixEpoch.Ticks,
            ["role"] = "Teacher", ["desktopRuntime"] = "", ["psk"] = "private-do-not-copy", ["password"] = "private-do-not-copy",
        };
        var snapshot = ClientStatusConnection.CapturePing(data);
        Assert.Equal("Close", snapshot.MachineName);
        Assert.Equal("Close", snapshot.UserName);
        Assert.Equal("id", snapshot.ClientId);
        Assert.Equal("01:02:03:04:05:06", snapshot.Mac);
        Assert.Equal("1.20", snapshot.Version);
        Assert.Equal("1.2.0.42", snapshot.AppVersion);
        Assert.Equal("x64", snapshot.Architecture);
        Assert.Equal("Windows 11", snapshot.WindowsVersion);
        Assert.Equal(3, snapshot.BlockedWebsiteCount);
        Assert.Equal([Cmd.ProcWindows], snapshot.Capabilities);
        Assert.True(snapshot.UserAgent);
        Assert.Equal("en-US", snapshot.InterfaceLanguage);
        Assert.Equal(500, snapshot.UptimeSeconds);
        Assert.Equal(DateTime.UnixEpoch.Ticks, snapshot.UtcTicks);
        Assert.Equal("Teacher", snapshot.Role);
        Assert.Equal("", snapshot.DesktopRuntime);
        Assert.DoesNotContain("private-do-not-copy", JsonSerializer.Serialize(snapshot));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.Capabilities!).Add("mutation"));
        data["capabilities"]!.AsArray().Clear();
        data["machineName"] = "changed";
        Assert.Single(snapshot.Capabilities!);
        Assert.Equal("Close", snapshot.MachineName);

        var old = ClientStatusConnection.CapturePing(new JsonObject());
        Assert.Null(old.Capabilities);
        Assert.Null(old.MachineName);
        Assert.Null(old.DesktopRuntime);
        Assert.Null(old.UtcTicks);
        Assert.Null(old.BlockedWebsiteCount);
        Assert.Equal("", old.Version);
        Assert.Empty(ClientStatusConnection.CapturePing(new JsonObject { ["capabilities"] = new JsonArray() }).Capabilities!);
        Assert.Equal("", ClientStatusConnection.CapturePing(new JsonObject { ["machineName"] = "" }).MachineName);
    }

    [Fact]
    public void Malformed_scalar_status_is_not_coerced_into_success() =>
        Assert.Throws<InvalidOperationException>(() => ClientStatusConnection.CapturePing(new JsonObject { ["blockedWebsiteCount"] = "not-a-number" }));

    static async Task<T> Pending<T>(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return default!;
    }

    [Fact]
    public async Task Ping_timeout_has_the_existing_status_error_and_optional_timeout_is_unknown()
    {
        var connection = new Connection { ReadPing = Pending<StatusPing>, ReadLanguage = Pending<string?> };
        var probe = new StatusProbe(pingTimeout: TimeSpan.FromMilliseconds(50), optionalTimeout: TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAsync<TimeoutException>(() => probe.PingAsync(connection).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("The client stopped answering.", error.Message);
        Assert.Null(await probe.InterfaceLanguageAsync(connection, true, true).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new StatusWindows(2, 3), await probe.OpenWindowsAsync(connection, true));
        Assert.False(connection.Disposed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Unsupported_or_absent_user_reads_are_not_sent(bool supported, bool hasUser)
    {
        var connection = new Connection();
        var probe = new StatusProbe();
        Assert.Null(await probe.InterfaceLanguageAsync(connection, supported, hasUser));
        Assert.Null(await probe.OpenWindowsAsync(connection, false));
        Assert.Equal(0, connection.Languages);
        Assert.Equal(0, connection.Windows);
    }

    [Fact]
    public async Task Optional_failures_do_not_replace_a_good_ping_and_other_optional_reads_continue()
    {
        var connection = new Connection { ReadLanguage = _ => throw new IOException() };
        var probe = new StatusProbe();
        Assert.NotNull((await probe.PingAsync(connection)).Ping);
        Assert.Null(await probe.InterfaceLanguageAsync(connection, true, true));
        Assert.Equal(new StatusWindows(2, 3), await probe.OpenWindowsAsync(connection, true));
        connection.ReadWindows = _ => throw new IOException();
        Assert.Null(await probe.OpenWindowsAsync(connection, true));
        Assert.False(connection.Disposed);
    }

    [Theory]
    [InlineData("ping")]
    [InlineData("language")]
    [InlineData("windows")]
    public async Task Caller_cancellation_is_not_a_timeout_or_unknown_metadata(string operation)
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new Connection
        {
            ReadPing = token => { entered.TrySetResult(); return Pending<StatusPing>(token); },
            ReadLanguage = token => { entered.TrySetResult(); return Pending<string?>(token); },
            ReadWindows = token => { entered.TrySetResult(); return Pending<StatusWindows>(token); },
        };
        var probe = new StatusProbe();
        Task work = operation switch
        {
            "ping" => probe.PingAsync(connection, stop.Token),
            "language" => probe.InterfaceLanguageAsync(connection, true, true, stop.Token),
            _ => probe.OpenWindowsAsync(connection, true, stop.Token),
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(connection.Disposed);
    }

    [Fact]
    public async Task Ping_preserves_authentication_failure_type()
    {
        var error = new AuthException("fixture");
        var connection = new Connection { ReadPing = _ => throw error };
        Assert.Same(error, await Assert.ThrowsAsync<AuthException>(() => new StatusProbe().PingAsync(connection)));
    }
}
