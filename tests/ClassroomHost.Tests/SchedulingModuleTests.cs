// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Automation;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-222: orchestration contracts without files, transport or Host types.</summary>
public sealed class SchedulingModuleTests
{
    static SchedulePlan Plan(string id = "s", bool catchUp = true, bool due = true, string? occurrence = "2026-10-26 08:15") =>
        new(id, id, catchUp, due, occurrence);
    static readonly ScheduleTarget One = new("one", "One", "one.invalid", "Lab");
    static readonly ScheduleTarget Two = new("two", "Two", "two.invalid", "Lab");
    static ScheduleResult Result(ScheduleTarget target, bool ok = true) => new(target.Computer, target.Address, target.Room, ok, "detail");

    sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 26, 6, 15, 0, TimeSpan.Zero);
    }

    sealed class Runtime : IScheduleRuntime
    {
        public Dictionary<string, Run> Runs { get; } = [];
        public List<string> Acquired { get; } = [];
        public List<Exception> Failures { get; } = [];
        public IScheduleRun? TryAcquire(SchedulePlan schedule)
        {
            Acquired.Add(schedule.Id);
            return Runs.GetValueOrDefault(schedule.Id);
        }
        public string DescribeFailure(Exception error) => error.GetType().Name;
        public ScheduleResult MissingResult(ScheduleTarget target) => new(target.Computer, target.Address, target.Room, false, "missing");
        public void TimedFailure(SchedulePlan schedule, Exception error) => Failures.Add(error);
    }

    sealed class Run : IScheduleRun
    {
        public bool Claim { get; set; } = true;
        public IReadOnlyList<ScheduleTarget> Targets { get; set; } = [One, Two];
        public Func<CancellationToken, Task> Timed { get; set; } = _ => Task.CompletedTask;
        public Func<IReadOnlyList<ScheduleTarget>, Action<string>, CancellationToken, Task<IReadOnlyList<ScheduleResult>>> Execute { get; set; } =
            (targets, _, _) => Task.FromResult<IReadOnlyList<ScheduleResult>>(targets.Select(target => Result(target)).ToArray());
        public bool Unknown { get; set; }
        public bool ReportFailure { get; set; } = true;
        public List<string> Events { get; } = [];
        public List<ScheduleResult> Marked { get; } = [];
        public List<(ScheduleResult Result, bool Unknown)> Reported { get; } = [];
        public DateTime ClaimedAt { get; private set; }
        public bool Disposed { get; private set; }
        public Task<bool> ClaimTimedAsync(CancellationToken cancellation) { Events.Add("claim-timed"); return Task.FromResult(Claim); }
        public IReadOnlyList<ScheduleTarget> ClaimTargets(DateTime utcNow) { ClaimedAt = utcNow; Events.Add("claim-targets"); return Targets; }
        public void BeforeDispatch(string key) { Events.Add("dispatch:" + key); }
        public Task ExecuteTimedAsync(CancellationToken cancellation) { Events.Add("execute-timed"); return Timed(cancellation); }
        public Task<IReadOnlyList<ScheduleResult>> ExecuteCatchUpAsync(IReadOnlyList<ScheduleTarget> targets, Action<string> beforeDispatch, CancellationToken cancellation)
        { Events.Add("execute"); return Execute(targets, beforeDispatch, cancellation); }
        public void MarkSuccessful(IReadOnlyList<ScheduleResult> results) { Events.Add("mark"); Marked.AddRange(results.Where(result => result.Ok)); }
        public bool IsUnknown(ScheduleResult result) => Unknown;
        public bool ShouldReportFailure(ScheduleResult result) => ReportFailure;
        public void Report(ScheduleResult result, bool uncertain) { Events.Add("report"); Reported.Add((result, uncertain)); }
        public void Dispose() { Disposed = true; Events.Add("dispose"); }
    }

    [Fact]
    public async Task Timed_admission_skips_catchup_not_due_held_and_already_claimed_schedules()
    {
        var runtime = new Runtime();
        var rejected = new Run { Claim = false };
        var admitted = new Run();
        runtime.Runs["claimed"] = rejected;
        runtime.Runs["ready"] = admitted;
        var module = new SchedulingModule(runtime);
        var count = await module.RunTimedAsync([Plan("catchup"), Plan("early", false, false), Plan("held", false),
            Plan("claimed", false), Plan("ready", false)]);
        Assert.Equal(1, count);
        Assert.Equal(["held", "claimed", "ready"], runtime.Acquired);
        Assert.Equal(["claim-timed", "dispose"], rejected.Events);
        Assert.Equal(["claim-timed", "execute-timed", "dispose"], admitted.Events);
    }

    [Fact]
    public async Task Timed_failure_is_reported_and_does_not_stop_the_next_schedule()
    {
        var first = new Run { Timed = _ => throw new TimeoutException() };
        var second = new Run();
        var runtime = new Runtime();
        runtime.Runs["first"] = first;
        runtime.Runs["second"] = second;
        Assert.Equal(2, await new SchedulingModule(runtime).RunTimedAsync([Plan("first", false), Plan("second", false)]));
        Assert.IsType<TimeoutException>(Assert.Single(runtime.Failures));
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task Catchup_preserves_scope_adds_missing_results_and_marks_before_reporting()
    {
        var run = new Run { Execute = (_, dispatch, _) =>
        {
            dispatch(One.Key);
            return Task.FromResult<IReadOnlyList<ScheduleResult>>([Result(One), new("Other", "outside.invalid", "", true, "ignored")]);
        }};
        var runtime = new Runtime();
        runtime.Runs["s"] = run;
        var results = await new SchedulingModule(runtime, new Clock()).RunCatchUpAsync([Plan()]);
        Assert.Equal([One.Address, Two.Address], results.Select(result => result.Result.Address));
        Assert.False(results[1].Result.Ok);
        Assert.Equal("missing", results[1].Result.Detail);
        Assert.Equal(One.Address, Assert.Single(run.Marked).Address);
        Assert.Equal(["claim-targets", "execute", "dispatch:one", "mark", "report", "report", "dispose"], run.Events);
        Assert.Equal(new Clock().GetUtcNow().UtcDateTime, run.ClaimedAt);
        Assert.All(results, result => Assert.Equal("s", result.ScheduleName));
    }

    [Fact]
    public async Task Execution_errors_become_failed_results_without_exposing_raw_exception_details()
    {
        var run = new Run { Execute = (_, _, _) => throw new IOException("private key and path") };
        var runtime = new Runtime();
        runtime.Runs["s"] = run;
        var results = await new SchedulingModule(runtime).RunCatchUpAsync([Plan()]);
        Assert.Equal(2, results.Count);
        Assert.All(results, result => { Assert.False(result.Result.Ok); Assert.Equal("IOException", result.Result.Detail); });
        Assert.Empty(run.Marked);
        Assert.True(run.Disposed);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public async Task Unknown_failures_are_reported_even_when_ordinary_repeated_failures_are_suppressed(bool unknown, int expected)
    {
        var run = new Run { Unknown = unknown, ReportFailure = false,
            Execute = (targets, _, _) => Task.FromResult<IReadOnlyList<ScheduleResult>>(targets.Select(target => Result(target, false)).ToArray()) };
        var runtime = new Runtime();
        runtime.Runs["s"] = run;
        await new SchedulingModule(runtime).RunCatchUpAsync([Plan()]);
        Assert.Equal(expected, run.Reported.Count);
        Assert.All(run.Reported, item => Assert.True(item.Unknown));
    }

    [Fact]
    public async Task Caller_cancellation_after_dispatch_releases_lease_without_success_checkpoint()
    {
        using var stop = new CancellationTokenSource();
        var run = new Run { Execute = (targets, dispatch, _) =>
        {
            dispatch(targets[0].Key);
            stop.Cancel();
            return Task.FromResult<IReadOnlyList<ScheduleResult>>(targets.Select(target => Result(target)).ToArray());
        }};
        var runtime = new Runtime();
        runtime.Runs["s"] = run;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SchedulingModule(runtime).RunCatchUpAsync([Plan()], stop.Token));
        Assert.True(run.Disposed);
        Assert.DoesNotContain("mark", run.Events);
        Assert.Empty(run.Reported);
    }

    [Fact]
    public async Task Dispatch_outside_claimed_scope_is_refused_before_the_core_checkpoint()
    {
        var run = new Run { Execute = (_, dispatch, _) => { dispatch("outside"); throw new Exception("unreachable"); } };
        var runtime = new Runtime();
        runtime.Runs["s"] = run;
        var results = await new SchedulingModule(runtime).RunCatchUpAsync([Plan()]);
        Assert.All(results, result => Assert.False(result.Result.Ok));
        Assert.DoesNotContain(run.Events, entry => entry.StartsWith("dispatch:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_terminal_answers_are_not_acknowledged_as_success()
    {
        var run = new Run { Execute = (_, _, _) => Task.FromResult<IReadOnlyList<ScheduleResult>>([Result(One), Result(One, false)]) };
        var runtime = new Runtime();
        runtime.Runs["s"] = run;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SchedulingModule(runtime).RunCatchUpAsync([Plan()]));
        Assert.Empty(run.Marked);
        Assert.DoesNotContain("mark", run.Events);
        Assert.True(run.Disposed);
    }

    [Fact]
    public async Task Duplicate_schedule_ids_fail_before_any_lease_or_checkpoint()
    {
        var runtime = new Runtime();
        await Assert.ThrowsAsync<ArgumentException>(() => new SchedulingModule(runtime).RunCatchUpAsync([Plan(), Plan()]));
        Assert.Empty(runtime.Acquired);
    }

    [Fact]
    public async Task Timed_cancellation_propagates_without_failure_callback_and_releases_ownership()
    {
        using var stop = new CancellationTokenSource();
        var run = new Run { Timed = cancellation =>
        {
            stop.Cancel();
            cancellation.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }};
        var runtime = new Runtime();
        runtime.Runs["s"] = run;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SchedulingModule(runtime).RunTimedAsync([Plan(catchUp: false)], stop.Token));
        Assert.Empty(runtime.Failures);
        Assert.True(run.Disposed);
    }

    [Fact]
    public void Assembly_has_only_framework_references()
    {
        FrameworkReferenceAssertions.AssertOnly(typeof(SchedulingModule).Assembly);
    }
}
