// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows.Threading;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

// T-222: recurring timer ownership, closure and dispatcher adapter contracts.
public sealed class RecurringWorkLifetimeTests
{
    sealed class Timer(Action tick) : IDisposable
    {
        public int Disposals { get; private set; }
        public void Fire() => tick(); // Also simulates an already queued tick after Stop.
        public void Dispose() => Disposals++;
    }

    [Fact]
    public void Independent_named_tasks_keep_their_intervals_and_do_not_run_immediately()
    {
        var timers = new List<(TimeSpan Interval, Timer Timer)>();
        using var lifetime = new RecurringWorkLifetime((interval, tick) =>
        {
            var timer = new Timer(tick);
            timers.Add((interval, timer));
            return timer;
        });
        var calls = new List<string>();
        Assert.True(lifetime.Start("operations", TimeSpan.FromSeconds(1), () => calls.Add("operations")));
        Assert.True(lifetime.Start("schedules", TimeSpan.FromSeconds(20), () => calls.Add("schedules")));
        Assert.True(lifetime.Start("refresh", TimeSpan.FromSeconds(30), () => calls.Add("refresh")));
        Assert.Empty(calls);
        Assert.Equal(new[] { 1d, 20d, 30d }, timers.Select(t => t.Interval.TotalSeconds));
        timers[2].Timer.Fire();
        timers[0].Timer.Fire();
        Assert.Equal(new[] { "refresh", "operations" }, calls);
    }

    [Fact]
    public void Repeated_start_keeps_the_original_callback_and_only_one_timer()
    {
        var timers = new List<Timer>();
        using var lifetime = new RecurringWorkLifetime((_, tick) =>
        {
            var timer = new Timer(tick);
            timers.Add(timer);
            return timer;
        });
        var first = 0;
        var duplicate = 0;
        Assert.True(lifetime.Start("schedules", TimeSpan.FromSeconds(20), () => first++));
        Assert.False(lifetime.Start("schedules", TimeSpan.FromSeconds(20), () => duplicate++));
        Assert.Single(timers).Fire();
        Assert.Equal(1, first);
        Assert.Equal(0, duplicate);
    }

    [Fact]
    public void Closure_stops_every_timer_once_and_suppresses_late_ticks_and_restart()
    {
        var timers = new List<Timer>();
        var calls = 0;
        var lifetime = new RecurringWorkLifetime((_, tick) =>
        {
            var timer = new Timer(tick);
            timers.Add(timer);
            return timer;
        });
        foreach (var name in new[] { "operations", "schedules", "refresh" })
            lifetime.Start(name, TimeSpan.FromSeconds(1), () => calls++);
        lifetime.Dispose();
        lifetime.Dispose();
        foreach (var timer in timers)
        {
            Assert.Equal(1, timer.Disposals);
            timer.Fire();
        }
        Assert.False(lifetime.Start("other", TimeSpan.FromSeconds(1), () => calls++));
        Assert.Equal(3, timers.Count);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_new_owner_runs_independently_of_the_closed_owner()
    {
        var timers = new List<Timer>();
        IDisposable Factory(TimeSpan _, Action tick)
        {
            var timer = new Timer(tick);
            timers.Add(timer);
            return timer;
        }
        var firstCalls = 0;
        var secondCalls = 0;
        using var first = new RecurringWorkLifetime(Factory);
        using var second = new RecurringWorkLifetime(Factory);
        first.Start("refresh", TimeSpan.FromSeconds(30), () => firstCalls++);
        second.Start("refresh", TimeSpan.FromSeconds(30), () => secondCalls++);
        first.Dispose();
        foreach (var timer in timers) timer.Fire();
        Assert.Equal(0, firstCalls);
        Assert.Equal(1, secondCalls);
    }

    [Fact]
    public async Task Closure_allows_already_dispatched_work_to_finish()
    {
        Timer? timer = null;
        using var lifetime = new RecurringWorkLifetime((_, tick) => timer = new Timer(tick));
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        lifetime.Start("schedules", TimeSpan.FromSeconds(20), async () =>
        {
            starts++;
            await resume.Task;
            completed.SetResult();
        });
        timer!.Fire();
        lifetime.Dispose();
        timer.Fire();
        resume.SetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, starts);
    }

    [Fact]
    public void Failed_creation_can_retry_and_the_failed_callback_is_disabled()
    {
        Action? lateTick = null;
        var attempts = 0;
        using var lifetime = new RecurringWorkLifetime((_, tick) =>
        {
            if (++attempts == 1) { lateTick = tick; throw new InvalidOperationException("fixture"); }
            return new Timer(tick);
        });
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => lifetime.Start("refresh", TimeSpan.FromSeconds(30), () => calls++));
        lateTick!();
        Assert.True(lifetime.Start("refresh", TimeSpan.FromSeconds(30), () => calls++));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Closure_during_creation_disposes_the_late_registration()
    {
        Timer? timer = null;
        RecurringWorkLifetime? lifetime = null;
        lifetime = new RecurringWorkLifetime((_, tick) =>
        {
            lifetime!.Dispose();
            return timer = new Timer(tick);
        });
        using (lifetime)
        {
            var calls = 0;
            Assert.False(lifetime.Start("refresh", TimeSpan.FromSeconds(30), () => calls++));
            Assert.Equal(1, timer!.Disposals);
            timer.Fire();
            Assert.Equal(0, calls);
        }
    }

    sealed class FailingTimer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Fact]
    public void Cleanup_failure_does_not_leave_other_sources_running()
    {
        var calls = 0;
        Action? lateTick = null;
        var stops = 0;
        using var lifetime = new RecurringWorkLifetime((_, tick) =>
        {
            lateTick = tick;
            return new FailingTimer(() =>
            {
                stops++;
                tick();
                throw new InvalidOperationException("fixture");
            });
        });
        lifetime.Start("schedules", TimeSpan.FromSeconds(20), () => calls++);
        lifetime.Start("refresh", TimeSpan.FromSeconds(30), () => calls++);
        Assert.Equal(2, Assert.Throws<AggregateException>(lifetime.Dispose).InnerExceptions.Count);
        lateTick!();
        Assert.Equal(2, stops);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Real_dispatcher_adapter_ticks_on_its_owner_and_stops_on_disposal()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var lifetime = new RecurringWorkLifetime((interval, tick) => new DispatcherRecurringTimer(dispatcher, interval, tick));
                var calls = 0;
                var frame = new DispatcherFrame();
                lifetime.Start("refresh", TimeSpan.FromMilliseconds(1), () =>
                {
                    Assert.True(dispatcher.CheckAccess());
                    calls++;
                    lifetime.Dispose();
                });
                using var finish = new DispatcherRecurringTimer(dispatcher, TimeSpan.FromMilliseconds(100), () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
                Assert.Equal(1, calls);
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Dispatcher fixture did not finish.");
        Assert.Null(failure);
    }
}
