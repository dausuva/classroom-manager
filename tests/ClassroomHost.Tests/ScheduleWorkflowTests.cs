// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-217: orchestration with injected execution, time and scratch records; no window or real settings.</summary>
public sealed class ScheduleWorkflowTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("cm-schedule-workflow-").FullName;
    static readonly DateTime Now = new(2026, 10, 26, 8, 15, 0);
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = new(2026, 10, 26, 6, 15, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
    readonly Clock _clock = new();
    string Daily => Path.Combine(_root, "daily.json");
    string Computers => Path.Combine(_root, "computers.json");
    ScheduleWorkflow Workflow => new(Daily, Computers, _clock);
    public void Dispose() => Directory.Delete(_root, true);

    static ScheduledAction Schedule(string id = "s", string action = "timesync") => new()
    { Id = id, Name = id, Action = action, Enabled = true, AllComputers = true, CatchUp = action != "lock", Days = ["Mon"], Time = "08:15" };
    static List<SavedComputer> Targets(ScheduledAction _) =>
        [new() { Name = "One", ClientId = "one", Address = "one.invalid" }, new() { Name = "Two", ClientId = "two", Address = "two.invalid" }];
    static CliResult Result(SavedComputer computer, bool ok, string detail = "ok") => new(computer.Name, computer.Address, "", ok, detail);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timed_failure_or_timeout_does_not_stop_other_schedules_or_repeat_the_failed_action(bool timeout)
    {
        var called = new List<string>();
        var failures = new List<Exception>();
        var schedules = new[] { Schedule("first", "lock"), Schedule("second", "lock") };
        Task Execute(ScheduledAction schedule, CancellationToken _)
        {
            called.Add(schedule.Id);
            if (schedule.Id == "first") throw timeout ? new TimeoutException() : new IOException();
            return Task.CompletedTask;
        }
        Assert.Equal(2, await Workflow.RunTimedAsync(schedules, Now, Execute, (_, ex) => failures.Add(ex)));
        Assert.Single(failures);
        Assert.Equal(["first", "second"], called);
        Assert.Equal(0, await Workflow.RunTimedAsync(schedules, Now, Execute, (_, _) => throw new Exception("Must not repeat")));
        using var recovered = ScheduleOwnership.TryAcquire("first", Computers);
        Assert.NotNull(recovered);
    }

    [Fact]
    public async Task Partial_success_is_committed_and_only_the_failed_computer_is_retried()
    {
        var schedule = Schedule();
        var log = new List<string>();
        var first = await Workflow.RunCatchUpAsync([schedule], Now, Targets,
            (_, computers, _, _) => Task.FromResult(computers.Select((pc, index) => Result(pc, index == 0, "timeout")).ToList()), log.Add);
        Assert.Equal(2, first.Count);
        Assert.Equal(1, ScheduleRuns.DoneCount("s", ScheduleRuns.OccurrenceKey(Now), ["one", "two"], Computers));
        _clock.Utc += TimeSpan.FromMinutes(6);
        var next = await Workflow.RunCatchUpAsync([schedule], Now, Targets, (_, computers, _, _) =>
        {
            Assert.Equal("two", Assert.Single(computers).ClientId);
            return Task.FromResult(computers.Select(pc => Result(pc, true)).ToList());
        }, log.Add);
        Assert.Single(next);
        Assert.Equal(2, ScheduleRuns.DoneCount("s", ScheduleRuns.OccurrenceKey(Now), ["one", "two"], Computers));
    }

    [Fact]
    public async Task An_active_command_is_owned_past_retry_time_and_a_lost_acknowledgement_is_not_replayed()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var schedule = Schedule(action: "command");
        var log = new List<string>();
        List<SavedComputer> One(ScheduledAction s) => [Targets(s)[0]];
        var active = Workflow.RunCatchUpAsync([schedule], Now, One, async (_, computers, dispatch, _) =>
        {
            dispatch(ScheduleRuns.ComputerKey(computers[0]));
            entered.SetResult();
            await release.Task;
            throw new TimeoutException("Acknowledgement lost");
        }, log.Add);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _clock.Utc += TimeSpan.FromHours(2);
        try
        {
            Assert.Empty(await Workflow.RunCatchUpAsync([schedule], Now, One,
                (_, _, _, _) => throw new Exception("Concurrent dispatch"), log.Add));
        }
        finally { release.SetResult(); }
        Assert.False(Assert.Single(await active).Ok);
        Assert.Contains(log, line => line.Contains("Outcome unknown"));
        Assert.Empty(await Workflow.RunCatchUpAsync([schedule], Now, One,
            (_, _, _, _) => throw new Exception("Unsafe retry"), log.Add));
    }

    [Fact]
    public async Task Cancellation_releases_ownership_and_preserves_uncertainty_after_dispatch()
    {
        using var stop = new CancellationTokenSource();
        var schedule = Schedule(action: "command");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Workflow.RunCatchUpAsync([schedule], Now,
            s => [Targets(s)[0]], async (_, computers, dispatch, token) =>
            {
                dispatch(ScheduleRuns.ComputerKey(computers[0]));
                stop.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return [];
            }, _ => { }, stop.Token));
        using var recovered = ScheduleOwnership.TryAcquire("s", Computers);
        Assert.NotNull(recovered);
        Assert.Equal(1, ScheduleRuns.UnknownCount("s", ScheduleRuns.OccurrenceKey(Now), ["one"], Computers));
    }

    [Fact]
    public async Task Missing_transport_results_are_failures_and_never_count_as_completed()
    {
        var results = await Workflow.RunCatchUpAsync([Schedule()], Now, Targets,
            (_, _, _, _) => Task.FromResult(new List<CliResult>()), _ => { });
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.False(result.Ok));
        Assert.Equal(0, ScheduleRuns.DoneCount("s", ScheduleRuns.OccurrenceKey(Now), ["one", "two"], Computers));
    }

    [Fact]
    public async Task Contradictory_terminal_results_preserve_dispatch_uncertainty_and_cannot_trigger_replay()
    {
        var schedule = Schedule(action: "command");
        List<SavedComputer> One(ScheduledAction s) => [Targets(s)[0]];
        await Assert.ThrowsAsync<InvalidOperationException>(() => Workflow.RunCatchUpAsync([schedule], Now, One,
            (_, computers, dispatch, _) =>
            {
                var computer = Assert.Single(computers);
                dispatch(ScheduleRuns.ComputerKey(computer));
                return Task.FromResult(new List<CliResult> { Result(computer, true), Result(computer, false) });
            }, _ => { }));
        var occurrence = ScheduleRuns.OccurrenceKey(Now);
        Assert.Equal(0, ScheduleRuns.DoneCount("s", occurrence, ["one"], Computers));
        Assert.Equal(1, ScheduleRuns.UnknownCount("s", occurrence, ["one"], Computers));
        _clock.Utc += TimeSpan.FromHours(2);
        Assert.Empty(await Workflow.RunCatchUpAsync([schedule], Now, One,
            (_, _, _, _) => throw new Exception("Must not replay"), _ => { }));
    }

    [Fact]
    public async Task Dispatch_outside_claimed_computers_cannot_create_an_external_uncertain_record()
    {
        var results = await Workflow.RunCatchUpAsync([Schedule(action: "command")], Now, s => [Targets(s)[0]],
            (_, _, dispatch, _) => { dispatch("outside"); throw new Exception("Must not dispatch"); }, _ => { });
        Assert.False(Assert.Single(results).Ok);
        Assert.Equal(0, ScheduleRuns.UnknownCount("s", ScheduleRuns.OccurrenceKey(Now), ["outside"], Computers));
        Assert.Equal(0, ScheduleRuns.DoneCount("s", ScheduleRuns.OccurrenceKey(Now), ["one"], Computers));
    }

    [Fact]
    public async Task Timed_definition_errors_fault_the_returned_task_instead_of_throwing_synchronously()
    {
        Task<int>? pending = null;
        var synchronous = Record.Exception(() =>
        {
            pending = Workflow.RunTimedAsync([Schedule("same", "lock"), Schedule("same", "lock")], Now,
                (_, _) => Task.CompletedTask, (_, _) => { });
        });
        Assert.Null(synchronous);
        Assert.NotNull(pending);
        await Assert.ThrowsAsync<ArgumentException>(() => pending);
    }
}
