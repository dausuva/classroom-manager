// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ManualDiscoveryWorkflowTests
{
    [Fact]
    public async Task Combined_enrolment_and_persistence_failures_remain_visible()
    {
        using var coordinator = new DiscoveryWorkCoordinator(TimeSpan.FromMinutes(1));
        var enrolment = new InvalidOperationException("synthetic enrolment");
        var persistence = new InvalidOperationException("synthetic persistence");
        var runtime = new Runtime { Enrol = () => throw enrolment, Saving = () => throw persistence };
        var error = await Assert.ThrowsAsync<AggregateException>(() => new ManualDiscoveryWorkflow(coordinator).RunAsync(runtime));
        Assert.Equal([enrolment, persistence], error.InnerExceptions);
        Assert.Equal("idle", runtime.Events.Last());
        Assert.False(coordinator.IsBusy);
    }

    sealed class Runtime : IManualDiscoveryRuntime<int>
    {
        public bool CanScan { get; set; } = true;
        public bool IsCurrent { get; set; } = true;
        public List<string> Events = [];
        public Func<CancellationToken, Task<int>> Discover = _ => Task.FromResult(7);
        public Func<Task> Enrol = () => Task.CompletedTask;
        public Action? Applying, Saving, Completing;
        public void SetScanning(bool scanning) => Events.Add(scanning ? "busy" : "idle");
        public void Prepare() => Events.Add("prepare");
        public Task<int> DiscoverAsync(CancellationToken cancellation) { Events.Add("discover"); return Discover(cancellation); }
        public void Apply(int batch) { Events.Add("apply:" + batch); Applying?.Invoke(); }
        public Task EnrolAsync(int batch) { Events.Add("enrol"); return Enrol(); }
        public void Save() { Events.Add("save"); Saving?.Invoke(); }
        public Task RefreshAsync() { Events.Add("refresh"); return Task.CompletedTask; }
        public void Completed() { Events.Add("completed"); Completing?.Invoke(); }
    }

    [Fact]
    public async Task Successful_scan_orders_admission_apply_enrol_save_refresh_and_releases_lease()
    {
        using var coordinator = new DiscoveryWorkCoordinator(TimeSpan.FromMinutes(1));
        var runtime = new Runtime();
        Assert.True(await new ManualDiscoveryWorkflow(coordinator).RunAsync(runtime));
        Assert.Equal(["busy", "prepare", "discover", "apply:7", "enrol", "save", "refresh", "completed", "idle"], runtime.Events);
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public void Accepted_state_and_persistence_resume_on_the_callers_dispatcher()
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
                    using var coordinator = new DiscoveryWorkCoordinator(TimeSpan.FromMinutes(1));
                    var runtime = new Runtime
                    {
                        Discover = async _ => { await Task.Delay(5); return 7; },
                        Enrol = async () => { await Task.Delay(5); Assert.True(dispatcher.CheckAccess()); },
                        Applying = () => Assert.True(dispatcher.CheckAccess()),
                        Saving = () => Assert.True(dispatcher.CheckAccess()),
                        Completing = () => Assert.True(dispatcher.CheckAccess()),
                    };
                    Assert.True(await new ManualDiscoveryWorkflow(coordinator).RunAsync(runtime));
                }
                catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        failure?.Throw();
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("busy")]
    [InlineData("closed")]
    public async Task Rejected_scan_does_not_touch_view_or_network(string reason)
    {
        using var coordinator = new DiscoveryWorkCoordinator(TimeSpan.FromMinutes(1));
        using var held = reason == "busy" ? coordinator.TryBeginScan() : null;
        if (reason == "closed") coordinator.Dispose();
        var runtime = new Runtime { CanScan = reason != "disabled" };
        Assert.False(await new ManualDiscoveryWorkflow(coordinator).RunAsync(runtime));
        Assert.Empty(runtime.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancel_or_changed_scope_after_late_discovery_cannot_apply_or_save(bool cancel)
    {
        using var coordinator = new DiscoveryWorkCoordinator(TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime();
        runtime.Discover = _ =>
        {
            if (cancel) cancellation.Cancel(); else runtime.IsCurrent = false;
            return Task.FromResult(7);
        };
        var operation = new ManualDiscoveryWorkflow(coordinator).RunAsync(runtime, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); else Assert.True(await operation);
        Assert.Equal(["busy", "prepare", "discover", "idle"], runtime.Events);
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public async Task Closure_during_accepted_enrolment_drains_and_saves_before_honoring_cancellation()
    {
        using var coordinator = new DiscoveryWorkCoordinator(TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime { Enrol = () => { cancellation.Cancel(); coordinator.Dispose(); return Task.CompletedTask; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ManualDiscoveryWorkflow(coordinator).RunAsync(runtime, cancellation.Token));
        Assert.Equal(["busy", "prepare", "discover", "apply:7", "enrol", "save", "idle"], runtime.Events);
    }

    [Theory]
    [InlineData("discover")]
    [InlineData("enrol")]
    [InlineData("save")]
    public async Task Failures_propagate_restore_scan_and_never_refresh_before_successful_save(string step)
    {
        using var coordinator = new DiscoveryWorkCoordinator(TimeSpan.FromMinutes(1));
        var failure = new InvalidOperationException("synthetic");
        var runtime = new Runtime();
        if (step == "discover") runtime.Discover = _ => throw failure;
        if (step == "enrol") runtime.Enrol = () => throw failure;
        if (step == "save") runtime.Saving = () => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => new ManualDiscoveryWorkflow(coordinator).RunAsync(runtime)));
        Assert.Equal("idle", runtime.Events.Last());
        Assert.Equal(step != "discover", runtime.Events.Contains("save"));
        Assert.DoesNotContain("refresh", runtime.Events);
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public async Task Scan_blocks_follower_and_second_manual_scan_until_accepted_enrolment_finishes()
    {
        using var coordinator = new DiscoveryWorkCoordinator(TimeSpan.FromMinutes(1));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime { Enrol = () => release.Task };
        var workflow = new ManualDiscoveryWorkflow(coordinator);
        var operation = workflow.RunAsync(runtime);
        Assert.Null(coordinator.TryBeginScan());
        var second = new Runtime();
        Assert.False(await workflow.RunAsync(second));
        Assert.Empty(second.Events);
        release.SetResult();
        Assert.True(await operation);
        Assert.False(coordinator.IsBusy);
    }
}
