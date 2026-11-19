// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

// T-222: scan admission/lifetime and core follower sequencing without network traffic or real settings.
public sealed class DiscoveryWorkCoordinatorTests
{
    sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => throw new Exception("Cadence must not use adjustable wall time");
    }

    sealed class Runtime : IAddressFollowRuntime<int, int>
    {
        public bool CanSearch { get; set; } = true;
        public List<int> Missing = [1];
        public List<int> Moved = [1];
        public List<string> Events = [];
        public List<Exception> Errors = [];
        public Func<CancellationToken, Task<IReadOnlyList<int>>> Discover = _ => Task.FromResult<IReadOnlyList<int>>([2]);
        public Func<int, Task> Refresh = _ => Task.CompletedTask;
        public Func<int, bool> Eligible = _ => true;
        public Action? Applying, Saving, Completing;
        public IReadOnlyList<int> CaptureMissing() { Events.Add("capture"); return Missing; }
        public Task<IReadOnlyList<int>> DiscoverAsync(CancellationToken cancellation) { Events.Add("discover"); return Discover(cancellation); }
        public IReadOnlyList<int> Apply(IReadOnlyList<int> missing, IReadOnlyList<int> answers)
        {
            Events.Add("apply:" + string.Join(',', missing));
            Applying?.Invoke();
            return Moved;
        }
        public void Save() { Events.Add("save"); Saving?.Invoke(); }
        public bool CanRefresh(int target) => Eligible(target);
        public Task RefreshAsync(int target) { Events.Add("refresh:" + target); return Refresh(target); }
        public void RefreshFailed(int target, Exception error) { Events.Add("refresh-failed:" + target); Errors.Add(error); }
        public void Completed() { Events.Add("completed"); Completing?.Invoke(); }
        public void Failed(Exception error) { Events.Add("failed"); Errors.Add(error); }
    }

    static DiscoveryWorkCoordinator Coordinator(Clock? clock = null) => new(TimeSpan.FromMinutes(1), clock);

    [Fact]
    public async Task Manual_scan_and_follower_share_admission_and_leases_are_idempotent()
    {
        using var work = Coordinator();
        var runtime = new Runtime();
        var manual = work.TryBeginScan();
        Assert.NotNull(manual);
        Assert.True(work.IsBusy);
        Assert.Null(work.TryBeginScan());
        Assert.False(await work.FollowAsync(runtime));
        Assert.Empty(runtime.Events);
        manual.Dispose();
        using var next = work.TryBeginScan();
        manual.Dispose(); // An old lease must not release its successor.
        Assert.True(work.IsBusy);
        Assert.Null(work.TryBeginScan());
    }

    [Fact]
    public async Task Batch_snapshots_membership_and_holds_admission_through_recovery_and_completion()
    {
        using var work = Coordinator();
        var found = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime { Discover = _ => found.Task };
        runtime.Completing = () => Assert.Null(work.TryBeginScan());
        var task = work.FollowAsync(runtime);
        Assert.Null(work.TryBeginScan());
        Assert.False(await work.FollowAsync(new Runtime()));
        runtime.Missing.Add(9);
        found.SetResult([2]);
        Assert.True(await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "capture", "discover", "apply:1", "save", "refresh:1", "save", "completed" }, runtime.Events);
        Assert.False(work.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_missing_or_disallowed_work_does_not_consume_cadence(bool disabled)
    {
        var clock = new Clock();
        using var work = Coordinator(clock);
        var runtime = new Runtime { CanSearch = !disabled, Missing = disabled ? [1] : [] };
        Assert.False(await work.FollowAsync(runtime));
        runtime.CanSearch = true;
        runtime.Missing = [1];
        Assert.True(await work.FollowAsync(runtime));
    }

    [Fact]
    public async Task Search_cadence_uses_monotonic_elapsed_time_and_allows_exact_interval()
    {
        var clock = new Clock();
        using var work = Coordinator(clock);
        Assert.True(await work.FollowAsync(new Runtime()));
        clock.Ticks = 59999;
        var skipped = new Runtime();
        Assert.False(await work.FollowAsync(skipped));
        Assert.Empty(skipped.Events);
        clock.Ticks++;
        Assert.True(await work.FollowAsync(new Runtime()));
    }

    [Theory]
    [InlineData("discovery")]
    [InlineData("apply")]
    [InlineData("save")]
    [InlineData("completed")]
    public async Task Failures_are_reported_release_admission_and_do_not_trigger_a_broadcast_loop(string phase)
    {
        var clock = new Clock();
        using var work = Coordinator(clock);
        var error = new IOException("fixture");
        var runtime = new Runtime();
        if (phase == "discovery") runtime.Discover = _ => throw error;
        if (phase == "apply") runtime.Applying = () => throw error;
        if (phase == "save") runtime.Saving = () => throw error;
        if (phase == "completed") runtime.Completing = () => throw error;
        Assert.True(await work.FollowAsync(runtime));
        Assert.Same(error, Assert.Single(runtime.Errors));
        Assert.False(work.IsBusy);
        Assert.False(await work.FollowAsync(new Runtime()));
        clock.Ticks = 60000;
        Assert.True(await work.FollowAsync(new Runtime()));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("close")]
    [InlineData("scope")]
    public async Task Late_discovery_does_not_apply_after_cancellation_closure_or_scope_change(string change)
    {
        using var work = Coordinator();
        using var stop = new CancellationTokenSource();
        var found = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime { Discover = _ => found.Task }; // Deliberately ignores cancellation.
        var task = work.FollowAsync(runtime, stop.Token);
        if (change == "cancel") stop.Cancel();
        if (change == "close") work.Dispose();
        if (change == "scope") runtime.CanSearch = false;
        found.SetResult([2]);
        if (change == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else Assert.True(await task);
        Assert.Equal(new[] { "capture", "discover" }, runtime.Events);
        Assert.Empty(runtime.Errors);
        Assert.False(work.IsBusy);
    }

    [Fact]
    public async Task Window_lifetime_closure_cancels_discovery_without_failure_or_commit()
    {
        using var work = Coordinator();
        using var window = new WindowWorkLifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime { Discover = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        } };
        var task = window.RunAsync(async token => await work.FollowAsync(runtime, token));
        await entered.Task;
        window.Dispose();
        work.Dispose();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "capture", "discover" }, runtime.Events);
        Assert.Empty(runtime.Errors);
        Assert.Equal(0, window.ActiveCount);
        Assert.Null(work.TryBeginScan());
    }

    [Fact]
    public async Task Closure_after_apply_still_drains_recovery_and_commits_accepted_state()
    {
        using var work = Coordinator();
        using var stop = new CancellationTokenSource();
        var runtime = new Runtime { Applying = () => { stop.Cancel(); work.Dispose(); } };
        Assert.True(await work.FollowAsync(runtime, stop.Token));
        Assert.Equal(new[] { "capture", "discover", "apply:1", "save", "refresh:1", "save", "completed" }, runtime.Events);
        Assert.Empty(runtime.Errors);
        Assert.False(work.IsBusy);
        Assert.Null(work.TryBeginScan());
    }

    [Fact]
    public async Task No_moves_do_not_save_or_refresh()
    {
        using var work = Coordinator();
        var runtime = new Runtime { Moved = [] };
        Assert.True(await work.FollowAsync(runtime));
        Assert.Equal(new[] { "capture", "discover", "apply:1" }, runtime.Events);
    }

    [Fact]
    public async Task Cancelled_search_consumes_cadence_but_pre_cancelled_admission_does_not()
    {
        var clock = new Clock();
        using var work = Coordinator(clock);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var runtime = new Runtime();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.FollowAsync(runtime, stop.Token));
        Assert.Empty(runtime.Events);
        Assert.False(work.IsBusy);
        runtime.Discover = _ => Task.FromCanceled<IReadOnlyList<int>>(stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.FollowAsync(runtime, stop.Token));
        // Cancel during a real attempt, rather than before admission.
        using var attempt = new CancellationTokenSource();
        runtime.Discover = _ => { attempt.Cancel(); return Task.FromResult<IReadOnlyList<int>>([]); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.FollowAsync(runtime, attempt.Token));
        Assert.False(await work.FollowAsync(new Runtime()));
        clock.Ticks = 60000;
        Assert.True(await work.FollowAsync(new Runtime()));
    }

    [Fact]
    public async Task Recovery_is_bounded_rechecks_queued_admission_and_isolates_target_errors()
    {
        using var work = Coordinator();
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var skipQueued = false;
        var runtime = new Runtime { Moved = Enumerable.Range(0, 18).ToList(), Eligible = target => target < 16 || !skipQueued,
            Refresh = async target =>
            {
                var count = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, count);
                if (count == 16) full.TrySetResult();
                await release.Task;
                Interlocked.Decrement(ref active);
                if (target == 0) throw new IOException("fixture");
            } };
        var task = work.FollowAsync(runtime);
        await full.Task.WaitAsync(TimeSpan.FromSeconds(5));
        skipQueued = true;
        release.SetResult();
        Assert.True(await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(16, maximum);
        Assert.DoesNotContain("refresh:16", runtime.Events);
        Assert.DoesNotContain("refresh:17", runtime.Events);
        Assert.Single(runtime.Errors);
        Assert.Equal("completed", runtime.Events.Last());
    }

    [Fact]
    public async Task Disposed_owner_rejects_new_work_without_capturing()
    {
        var work = Coordinator();
        work.Dispose();
        work.Dispose();
        var runtime = new Runtime();
        Assert.False(await work.FollowAsync(runtime));
        Assert.Empty(runtime.Events);
        Assert.Null(work.TryBeginScan());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_cadence_is_rejected(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiscoveryWorkCoordinator(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Discovery_application_and_recovery_resume_on_the_callers_dispatcher()
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
                    using var work = Coordinator();
                    var runtime = new Runtime { Discover = async _ => { await Task.Delay(5); return new[] { 2 }; },
                        Applying = () => Assert.True(dispatcher.CheckAccess()), Saving = () => Assert.True(dispatcher.CheckAccess()),
                        Completing = () => Assert.True(dispatcher.CheckAccess()),
                        Refresh = async _ => { await Task.Delay(5); Assert.True(dispatcher.CheckAccess()); } };
                    Assert.True(await work.FollowAsync(runtime));
                    Assert.Empty(runtime.Errors);
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
