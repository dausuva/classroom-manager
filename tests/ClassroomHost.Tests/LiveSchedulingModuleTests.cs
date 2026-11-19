// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LiveSchedulingFactAttribute : FactAttribute
{
    public LiveSchedulingFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_SCHEDULE_TEST_ADDRESS")))
            Skip = "Opt-in scheduling orchestration probe; Ping only, isolated history, explicit installed target/settings required.";
    }
}

/// <summary>T-222: no real schedule, OS task registration, clock adjustment or command execution.</summary>
[Collection(nameof(HostBackupTests))]
public sealed class LiveSchedulingModuleTests(ITestOutputHelper output)
{
    [LiveSchedulingFact]
    public async Task Installed_client_ping_is_checkpointed_once_through_the_module_using_isolated_history()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_SCHEDULE_TEST_ADDRESS")!;
        var settingsPath = Environment.GetEnvironmentVariable("CLASSROOM_SCHEDULE_TEST_SETTINGS");
        Assert.False(string.IsNullOrWhiteSpace(settingsPath));
        var before = File.ReadAllBytes(settingsPath!);
        var keysPath = Path.ChangeExtension(settingsPath!, ".keys");
        var keysBefore = File.ReadAllBytes(keysPath);
        var suspended = HostSettings.SavesSuspended;
        HostSettings.SavesSuspended = true;
        var scratch = Directory.CreateTempSubdirectory("cm-schedule-live-").FullName;
        try
        {
            var settings = HostSettings.Load(settingsPath);
            var saved = Assert.Single(settings.Computers, computer => computer.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            var client = new ClientInfo { Address = saved.Address, Port = saved.Port, ClientId = saved.ClientId, Mac = saved.Mac,
                KeyId = saved.KeyId, ComputerName = saved.Name, Alias = saved.Alias };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var now = DateTime.Now;
            // Use existing admission semantics, but deliberately substitute a read-only Ping executor.
            // This is NOT acceptance of the production timesync action or the OS background task.
            var schedule = new ScheduledAction { Id = "isolated-read-only-probe", Name = "Read-only probe", Action = "timesync",
                CatchUp = true, Enabled = true, Days = [now.DayOfWeek.ToString()[..3]], Time = now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) };
            var computersPath = Path.Combine(scratch, "computers.json");
            var workflow = new ScheduleWorkflow(Path.Combine(scratch, "daily.json"), computersPath);
            var executions = 0;
            async Task<List<CliResult>> Probe(ScheduledAction _, List<SavedComputer> selected, Action<string> dispatch, CancellationToken cancellation)
            {
                Assert.Equal(saved.Address, Assert.Single(selected).Address);
                using var session = await ClientSession.OpenAsync(client, settings, cancellation);
                dispatch(ScheduleRuns.ComputerKey(saved));
                var ping = await session.CallAsync(Cmd.Ping, ct: cancellation);
                Assert.False(string.IsNullOrWhiteSpace(ping["version"]?.GetValue<string>()));
                executions++;
                return [new(client.Name, client.Address, saved.Room, true, "Read-only Ping acknowledged")];
            }
            var results = await workflow.RunCatchUpAsync([schedule], now, _ => [saved], Probe, _ => { }, deadline.Token);
            Assert.True(Assert.Single(results).Ok);
            Assert.Empty(await workflow.RunCatchUpAsync([schedule], now, _ => [saved], Probe, _ => { }, deadline.Token));
            Assert.Equal(1, executions);
            var occurrence = ScheduleRuns.OccurrenceKey(ScheduleRuns.LatestOccurrence(schedule, now)!.Value);
            Assert.Equal(1, ScheduleRuns.DoneCount(schedule.Id, occurrence, [ScheduleRuns.ComputerKey(saved)], computersPath));
            output.WriteLine($"Installed scheduling orchestration probe: {client.Name}; Ping calls={executions}; isolated completion=1; repeat skipped.");
        }
        finally
        {
            HostSettings.SavesSuspended = suspended;
            try
            {
                Assert.Equal(before, File.ReadAllBytes(settingsPath!));
                Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
            }
            finally { Directory.Delete(scratch, true); }
        }
    }
}
