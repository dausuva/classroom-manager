// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-216: active ownership, interrupted records and uncertain command results.</summary>
public sealed class ScheduleReliabilityTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("cm-schedule-reliability-").FullName;
    string Record => Path.Combine(_root, "computers.json");
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Active_ownership_survives_retry_intervals_and_new_occurrences()
    {
        var now = DateTime.UtcNow;
        using (var first = ScheduleOwnership.TryAcquire("s", Record))
        {
            Assert.NotNull(first);
            ScheduleRuns.ClaimDue("s", "Monday", ["pc"], now, Record);
            Assert.Null(ScheduleOwnership.TryAcquire("s", Record));
            // Advancing retry time cannot release the OS-owned execution lease.
            Assert.Equal(0, ScheduleRuns.DoneCount("s", "Tuesday", ["pc"], Record));
            Assert.Null(ScheduleOwnership.TryAcquire("s", Record));
            using var other = ScheduleOwnership.TryAcquire("different-schedule", Record);
            Assert.NotNull(other);
        }
        using var recovered = ScheduleOwnership.TryAcquire("s", Record);
        Assert.NotNull(recovered);
        Assert.Single(ScheduleRuns.ClaimDue("s", "Monday", ["pc"], now.AddHours(2), Record));
    }

    [Fact]
    public void An_unacknowledged_command_is_not_repeated_after_its_owner_exits()
    {
        var now = DateTime.UtcNow;
        using (var owner = ScheduleOwnership.TryAcquire("s", Record))
        {
            ScheduleRuns.ClaimDue("s", "Monday", ["pc1", "pc2"], now, Record);
            ScheduleRuns.MarkDispatch("s", "Monday", "pc1", owner!.Owner, Record);
            ScheduleRuns.MarkDone("s", "Monday", ["pc2"], Record);
        }
        using var next = ScheduleOwnership.TryAcquire("s", Record);
        Assert.NotNull(next);
        Assert.Empty(ScheduleRuns.ClaimDue("s", "Monday", ["pc1", "pc2"], now.AddDays(1), Record));
        Assert.Equal(1, ScheduleRuns.UnknownCount("s", "Monday", ["pc1", "pc2"], Record));
        Assert.Equal(1, ScheduleRuns.DoneCount("s", "Monday", ["pc1", "pc2"], Record));
    }

    [Fact]
    public void Successful_acknowledgement_clears_uncertainty_and_abandoned_temporary_writes_are_ignored()
    {
        ScheduleRuns.ClaimDue("s", "run", ["pc"], DateTime.UtcNow, Record);
        ScheduleRuns.MarkDispatch("s", "run", "pc", "owner", Record);
        File.WriteAllText(Record + ".tmp-abandoned", "half written");
        ScheduleRuns.MarkDone("s", "run", ["pc"], Record);
        Assert.Equal(0, ScheduleRuns.UnknownCount("s", "run", ["pc"], Record));
        Assert.Equal(1, ScheduleRuns.DoneCount("s", "run", ["pc"], Record));
        Assert.True(File.Exists(Record + ".bak"));
        File.WriteAllText(Record, "half written");
        Assert.Throws<JsonException>(() => ScheduleRuns.ClaimDue("s", "run", ["pc"], DateTime.UtcNow.AddHours(1), Record));
    }

    [Fact]
    public async Task A_killed_runner_releases_its_os_lease_but_not_its_uncertain_record()
    {
        ScheduleRuns.ClaimDue("s", "run", ["pc"], DateTime.UtcNow, Record);
        ScheduleRuns.MarkDispatch("s", "run", "pc", "crashed-owner", Record);
        var path = ScheduleOwnership.LockPath("s", Record);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ready = Path.Combine(_root, "ready");
        var script = Path.Combine(_root, "hold.ps1");
        File.WriteAllText(script, "param($lockPath,$ready)\n$held=[IO.File]::Open($lockPath,'OpenOrCreate','ReadWrite','None')\n[IO.File]::WriteAllText($ready,'ready')\nStart-Sleep -Seconds 60\n$held.Dispose()\n");
        // The existing script harness kills only this synthetic child when its deadline expires.
        var child = PowerShellScript.RunAsync(PowerShellEdition.Windows, script, [path, ready], TimeSpan.FromSeconds(12));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(ready)) await Task.Delay(20, deadline.Token);
            Assert.Null(ScheduleOwnership.TryAcquire("s", Record));
        }
        finally { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => child); }
        ScheduleOwnership? recovered = null;
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while ((recovered = ScheduleOwnership.TryAcquire("s", Record)) is null) await Task.Delay(20, deadline.Token);
        using (recovered) Assert.Equal(1, ScheduleRuns.UnknownCount("s", "run", ["pc"], Record));
    }
}
