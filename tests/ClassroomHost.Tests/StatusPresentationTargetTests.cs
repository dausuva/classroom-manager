// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

// T-222: actual UI-state adapter, without real windows, connections, credentials or settings.
public sealed class StatusPresentationTargetTests
{
    static readonly StatusHostProtocol Host = new(1, 20, "1.20");
    static StatusObservation Observation(StatusPing? ping = null) => new(ping ?? new(), new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc), 100);
    static ClientInfo Client() => new() { Address = "192.0.2.10", ClientId = "fixture-id", Mac = "AAAAAAAAAAAA", Version = "1.20",
        Room = "fixture", Alias = "Close", User = "Close", KeyId = "fixture-fingerprint", Role = "Teacher", ExamModeApplied = true };

    [Fact]
    public void Metadata_replaces_capabilities_and_display_fields_without_touching_identity_or_recovery()
    {
        var client = Client();
        client.Capabilities.Add("old-command");
        client.Unreachable = true;
        var verified = 0;
        var target = new StatusPresentationTarget(StatusRefreshTarget.Capture(client), new[] { client }, () => verified++);
        var observation = Observation(new() { Version = "1.20", AppVersion = "1.2.0.42", Architecture = "x86", WindowsVersion = "Close", Capabilities = [] });
        Assert.True(target.ApplyMetadata(StatusProjection.Metadata(observation, new DateTime(2026, 10, 27, 15, 4, 5))));
        Assert.True(client.CapabilitiesAdvertised);
        Assert.Empty(client.Capabilities);
        Assert.False(client.Unreachable);
        Assert.Equal("100 ms", client.Ping);
        Assert.Equal("15:04:05", client.LastSeen);
        Assert.Equal("1.2.0.42", client.AppVersion);
        Assert.Equal("x86", client.Architecture);
        Assert.Equal("Close", client.WindowsVersion);
        Assert.True(target.ApplyMetadata(StatusProjection.Metadata(Observation(), DateTime.Now)));
        Assert.False(client.CapabilitiesAdvertised);
        Assert.Equal(2, verified);
        Assert.Equal("192.0.2.10", client.Address);
        Assert.Equal("fixture-id", client.ClientId);
        Assert.Equal("AAAAAAAAAAAA", client.Mac);
        Assert.Equal("fixture", client.Room);
        Assert.Equal("Close", client.Alias);
        Assert.Equal("Close", client.User);
        Assert.Equal("fixture-fingerprint", client.KeyId);
        Assert.Equal("Teacher", client.Role);
        Assert.True(client.ExamModeApplied);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("port")]
    [InlineData("id")]
    [InlineData("mac")]
    [InlineData("removed")]
    public void Stale_targets_reject_every_projection_phase_without_notifications(string change)
    {
        var client = Client();
        var roster = new List<ClientInfo> { client };
        var target = new StatusPresentationTarget(StatusRefreshTarget.Capture(client), roster, () => { });
        switch (change)
        {
            case "address": client.Address = "192.0.2.11"; break;
            case "port": client.Port++; break;
            case "id": client.ClientId = "different"; break;
            case "mac": client.Mac = "BBBBBBBBBBBB"; break;
            case "removed": roster.Clear(); break;
        }
        var changed = 0;
        client.PropertyChanged += (_, _) => changed++;
        Assert.False(target.ApplyMetadata(StatusProjection.Metadata(Observation(), DateTime.Now)));
        Assert.False(target.ApplyLanguage("lt-LT"));
        Assert.False(target.ApplyWindows("2 / 3"));
        Assert.False(target.ApplyOnline(Observation(), Host));
        Assert.Equal(0, changed);
        Assert.Equal("Unknown", client.Status);
        Assert.Equal("1.20", client.Version);
    }

    [Fact]
    public void Dispatcher_verification_precedes_every_write()
    {
        var client = Client();
        var error = new InvalidOperationException("fixture wrong thread");
        var target = new StatusPresentationTarget(StatusRefreshTarget.Capture(client), new[] { client }, () => throw error);
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => target.ApplyMetadata(StatusProjection.Metadata(Observation(), DateTime.Now))));
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => target.ApplyLanguage("lt-LT")));
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => target.ApplyWindows("2 / 3")));
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => target.ApplyOnline(Observation(), Host)));
        Assert.Equal("Unknown", client.Status);
        Assert.Equal("", client.Ping);
        Assert.Equal("", client.InterfaceLanguage);
        Assert.Equal("", client.OpenWindows);
    }

    [Fact]
    public void Legacy_absent_timing_retains_previous_values_and_online_state_does_not_erase_optional_reads()
    {
        var client = Client();
        client.Uptime = "old uptime";
        client.Clock = "old clock";
        client.ClockOffsetSec = 3;
        var target = new StatusPresentationTarget(StatusRefreshTarget.Capture(client), new[] { client }, () => { });
        Assert.True(target.ApplyLanguage("lt-LT"));
        Assert.True(target.ApplyWindows("0 / 0"));
        Assert.True(target.ApplyOnline(Observation(), Host));
        Assert.Equal("old uptime", client.Uptime);
        Assert.Equal("old clock", client.Clock);
        Assert.Equal(3, client.ClockOffsetSec);
        Assert.Equal("lt-LT", client.InterfaceLanguage);
        Assert.Equal("0 / 0", client.OpenWindows);
        Assert.Equal("idle", client.Level);
        Assert.Equal("Online - nobody signed in", client.Status);
    }

    [Fact]
    public void Timing_and_compatibility_are_applied_before_core_recovery_and_invalid_clock_still_faults()
    {
        var client = Client();
        client.Version = "1.19";
        var target = new StatusPresentationTarget(StatusRefreshTarget.Capture(client), new[] { client }, () => { });
        var observation = Observation(new() { UserAgent = true, UptimeSeconds = 3660 });
        observation = observation with { Ping = observation.Ping with { UtcTicks = observation.SentUtc.AddMilliseconds(50).AddSeconds(6).Ticks } };
        Assert.True(target.ApplyOnline(observation, Host));
        Assert.Equal("1h 1m", client.Uptime);
        Assert.Equal(6, client.ClockOffsetSec);
        Assert.EndsWith("  !", client.Clock);
        Assert.Equal("warn", client.Level);
        Assert.Equal("Online" + client.CompatibilityWarning, client.Status);
        var before = client.Status;
        Assert.Throws<ArgumentOutOfRangeException>(() => target.ApplyOnline(Observation(new() { UtcTicks = -1, UptimeSeconds = 120 }), Host));
        Assert.Equal("2m", client.Uptime); // Preserve the existing phased update, not a new atomic rollback claim.
        Assert.Equal(before, client.Status);
    }

    [Fact]
    public void Actual_dispatcher_accepts_continuations_rejects_workers_and_ignores_late_identity_results()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var client = Client();
                    var target = new StatusPresentationTarget(StatusRefreshTarget.Capture(client), new[] { client }, dispatcher.VerifyAccess);
                    client.PropertyChanged += (_, _) => Assert.True(dispatcher.CheckAccess());
                    await Task.Delay(5);
                    Assert.True(target.ApplyMetadata(StatusProjection.Metadata(Observation(), DateTime.Now)));
                    await Assert.ThrowsAsync<InvalidOperationException>(async () => await Task.Run(() => target.ApplyLanguage("worker")));
                    client.Address = "192.0.2.11";
                    await Task.Delay(5);
                    Assert.False(target.ApplyLanguage("late"));
                    Assert.Equal("", client.InterfaceLanguage);
                }
                catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        failure?.Throw();
    }
}
