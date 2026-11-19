// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

// T-222: core refresh ownership, independent of windows, transport and production settings.
public sealed class StatusRefreshWorkflowTests
{
    [Theory]
    [InlineData("", 0, "", false)]
    [InlineData("room", 0, "", true)]
    [InlineData("room", 1, "", false)]
    [InlineData("room", 0, "fixture update", false)]
    public void Initial_admission_requires_a_managed_idle_nonexclusive_computer(string room, int busy, string exclusive, bool expected) =>
        Assert.Equal(expected, StatusRefreshTarget.IsIdleManaged(new ClientInfo { Room = room, BusyCount = busy, ExclusiveOperation = exclusive }));

    [Fact]
    public async Task Default_batch_is_bounded_snapshotted_and_single_flight_through_completion()
    {
        var workflow = new StatusRefreshWorkflow();
        var targets = Enumerable.Range(0, 20).ToList();
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var maximum = 0;
        var called = new List<int>();
        var completions = 0;
        var batch = workflow.RunAsync(() => targets, _ => true, async (target, _) =>
        {
            lock (called)
            {
                called.Add(target);
                maximum = Math.Max(maximum, ++running);
                if (running == 16) full.TrySetResult();
            }
            await release.Task;
            lock (called) running--;
        }, (_, _) => throw new Exception(), () => { Assert.True(workflow.IsRunning); completions++; });
        await full.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(workflow.IsRunning);
        Assert.Equal(16, maximum);
        Assert.False(await workflow.RunAsync<int>(() => throw new Exception("Must not capture overlap"), _ => true,
            (_, _) => Task.CompletedTask, (_, _) => { }, () => throw new Exception()));
        targets.Add(999);
        release.SetResult();
        Assert.True(await batch.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(Enumerable.Range(0, 20), called.Order());
        Assert.Equal(1, completions);
        Assert.False(workflow.IsRunning);
        Assert.True(await workflow.RunAsync<int>(() => [], _ => true, (_, _) => Task.CompletedTask, (_, _) => { }, () => completions++));
        Assert.Equal(2, completions);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("exclusive")]
    [InlineData("room")]
    [InlineData("address")]
    [InlineData("port")]
    [InlineData("id")]
    [InlineData("mac")]
    [InlineData("removed")]
    [InlineData("network")]
    public async Task Queued_targets_are_revalidated_without_overwriting_their_status(string change)
    {
        var first = new ClientInfo { Room = "Room", Address = "first" };
        var queued = new ClientInfo { Room = "Room", Address = "queued", ClientId = "id", Mac = "mac", Status = "original" };
        var members = new List<ClientInfo> { first, queued };
        var selectedNetwork = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<ClientInfo>();
        var workflow = new StatusRefreshWorkflow(1);
        var batch = workflow.RunAsync(() => members.Select(StatusRefreshTarget.Capture).ToArray(),
            target => members.Contains(target.Client) && (target.Client == first || selectedNetwork) && target.CanStart(), async (target, _) =>
            {
                calls.Add(target.Client);
                entered.TrySetResult();
                await release.Task;
            }, (_, _) => throw new Exception(), () => { });
        await entered.Task;
        switch (change)
        {
            case "busy": queued.BusyCount++; break;
            case "exclusive": queued.ExclusiveOperation = "fixture update"; break;
            case "room": queued.Room = ""; break;
            case "address": queued.Address = "different"; break;
            case "port": queued.Port++; break;
            case "id": queued.ClientId = "different"; break;
            case "mac": queued.Mac = "different"; break;
            case "removed": members.Remove(queued); break;
            case "network": selectedNetwork = false; break;
        }
        release.SetResult();
        Assert.True(await batch.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(first, Assert.Single(calls));
        Assert.Equal("original", queued.Status);
    }

    [Fact]
    public async Task Per_computer_failure_is_isolated_and_finalization_follows_all_results()
    {
        var workflow = new StatusRefreshWorkflow();
        var calls = new List<int>();
        var errors = new List<int>();
        Assert.True(await workflow.RunAsync(() => new[] { 1, 2, 3 }, _ => true, (target, _) =>
        {
            calls.Add(target);
            return target == 2 ? Task.FromException(new IOException()) : Task.CompletedTask;
        }, (target, _) => errors.Add(target), () => { Assert.Equal(3, calls.Count); Assert.Single(errors); }));
        Assert.Equal([2], errors);
        Assert.False(workflow.IsRunning);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("failure handler")]
    [InlineData("completion")]
    public async Task Infrastructure_failures_fault_the_Task_and_release_admission(string phase)
    {
        var workflow = new StatusRefreshWorkflow();
        var failure = new InvalidOperationException("fixture");
        Task<bool>? task = null;
        Assert.Null(Record.Exception(() => { task = workflow.RunAsync<int>(() => phase == "capture" ? throw failure : [1],
            _ => true, (_, _) => phase == "failure handler" ? Task.FromException(new IOException()) : Task.CompletedTask,
            (_, _) => throw failure, () => { if (phase == "completion") throw failure; }); }));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => task!));
        Assert.False(workflow.IsRunning);
        Assert.True(await workflow.RunAsync<int>(() => [], _ => true, (_, _) => Task.CompletedTask, (_, _) => { }, () => { }));
    }

    [Fact]
    public async Task Cancellation_drains_running_work_skips_finalization_and_does_not_report_offline()
    {
        var workflow = new StatusRefreshWorkflow(1);
        using var stop = new CancellationTokenSource();
        var timeout = TimeSpan.FromSeconds(30); // Hang guard, not a cancellation latency requirement under release-build load.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<int>();
        var failures = 0;
        var completions = 0;
        var batch = workflow.RunAsync(() => new[] { 1, 2 }, _ => true, async (target, token) =>
        {
            calls.Add(target);
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                draining.SetResult();
                await release.Task;
            }
        }, (_, _) => failures++, () => completions++, stop.Token);
        try
        {
            await entered.Task.WaitAsync(timeout);
            stop.Cancel();
            await draining.Task.WaitAsync(timeout);
            Assert.True(workflow.IsRunning);
            Assert.False(batch.IsCompleted);
            Assert.False(await workflow.RunAsync<int>(() => throw new Exception("Must not admit overlap while draining"),
                _ => true, (_, _) => Task.CompletedTask, (_, _) => { }, () => { }));
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch).WaitAsync(timeout);
            Assert.Equal([1], calls); // The queued target must never begin after cancellation.
            Assert.Equal(0, failures); // Cancellation is not an offline result.
            Assert.Equal(0, completions);
            Assert.False(workflow.IsRunning);
            Assert.True(await workflow.RunAsync<int>(() => [], _ => true, (_, _) => Task.CompletedTask,
                (_, _) => { }, () => { }));
        }
        finally
        {
            stop.Cancel();
            release.TrySetResult();
            try { await batch.WaitAsync(timeout); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    [Fact]
    public void Target_identity_ignores_presentation_changes_but_preserves_endpoint_membership()
    {
        var client = new ClientInfo { Address = "host", ClientId = "id", Mac = "mac", Room = "room" };
        var target = StatusRefreshTarget.Capture(client);
        client.Alias = "new label";
        client.User = "new user";
        client.Version = "1.20";
        Assert.True(target.Matches());
        Assert.True(target.CanStart());
        client.Room = "";
        Assert.True(target.Matches());
        Assert.False(target.CanStart());
        client.Room = "room";
        client.Mac = "another";
        Assert.False(target.Matches());
        Assert.Same(client, target.Client);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_parallelism_is_rejected(int limit) => Assert.Throws<ArgumentOutOfRangeException>(() => new StatusRefreshWorkflow(limit));

    [Fact]
    public void Capture_callbacks_results_and_finalization_stay_on_the_dispatcher()
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
                    var workflow = new StatusRefreshWorkflow(1);
                    Assert.True(await workflow.RunAsync(() => { Assert.True(dispatcher.CheckAccess()); return new[] { 1, 2 }; },
                        _ => { Assert.True(dispatcher.CheckAccess()); return true; }, async (target, _) =>
                        {
                            Assert.True(dispatcher.CheckAccess());
                            await Task.Delay(5);
                            Assert.True(dispatcher.CheckAccess());
                            if (target == 1) throw new IOException();
                        }, (_, _) => Assert.True(dispatcher.CheckAccess()), () => Assert.True(dispatcher.CheckAccess())));
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
