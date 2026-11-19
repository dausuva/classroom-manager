// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Client;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>The Client keeps the computer awake only while it moves files or runs a long job (T-214).</summary>
[Collection("SleepGuard")]
public sealed class SleepGuardTests
{
    [Theory]
    [InlineData(Cmd.FsRead, true)]
    [InlineData(Cmd.FsWrite, true)]
    [InlineData(Cmd.FsRecent, true)]
    [InlineData(Cmd.WindowsUpdateScan, true)]
    [InlineData(Cmd.InstallRun, true)]
    [InlineData(Cmd.SelfUpdate, true)]
    [InlineData(Cmd.Ping, false)]
    [InlineData(Cmd.ProcWindows, false)]
    [InlineData(Cmd.LockSet, false)]
    [InlineData(Cmd.MessageShow, false)]
    public void Only_transfers_and_long_jobs_keep_the_computer_awake_never_the_status_pings(string command, bool keepsAwake) =>
        Assert.Equal(keepsAwake, SleepGuard.KeepsAwake(command));

    [Fact]
    public async Task Native_request_spans_chunks_expires_after_inactivity_and_can_be_released_on_another_thread()
    {
        var transfer = SleepGuard.Enter(Cmd.FsWrite)!;
        Assert.True(SleepGuard.HasPowerRequest);
        transfer.Dispose();
        Assert.True(SleepGuard.HasPowerRequest); // The next chunk has not arrived yet.
        Assert.Null(SleepGuard.Enter(Cmd.Ping));
        using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(55)))
            while (SleepGuard.HasPowerRequest) await Task.Delay(250, limit.Token);
        var job = await Task.Run(() => SleepGuard.Enter(Cmd.InstallRun)!);
        Assert.True(SleepGuard.HasPowerRequest);
        await Task.Run(job.Dispose);
        Assert.False(SleepGuard.HasPowerRequest);
    }

    [Fact]
    public void A_long_command_counts_while_it_runs_and_not_after()
    {
        var before = SleepGuard.Active;
        Assert.Null(SleepGuard.Enter(Cmd.Ping));
        var first = SleepGuard.Enter(Cmd.FsRead)!;
        var second = SleepGuard.Enter(Cmd.WindowsUpdateScan)!;
        Assert.Equal(before + 2, SleepGuard.Active);
        first.Dispose();
        first.Dispose();   // twice does not count twice
        Assert.Equal(before + 1, SleepGuard.Active);
        second.Dispose();
        Assert.Equal(before, SleepGuard.Active);
    }
}

[CollectionDefinition("SleepGuard", DisableParallelization = true)]
public sealed class SleepGuardGroup;
