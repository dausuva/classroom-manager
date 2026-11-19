// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

// T-222: window-owned observational work stops without turning closure into an error.
public sealed class WindowWorkLifetimeTests
{
    [Fact]
    public async Task Work_runs_with_a_live_token_and_releases_its_registration()
    {
        using var lifetime = new WindowWorkLifetime();
        var token = CancellationToken.None;

        await lifetime.RunAsync(ct =>
        {
            token = ct;
            Assert.False(ct.IsCancellationRequested);
            return Task.CompletedTask;
        });

        Assert.True(token.CanBeCanceled);
        Assert.Equal(0, lifetime.ActiveCount);
    }

    [Fact]
    public async Task Closure_cancels_every_active_job_and_is_a_normal_completion()
    {
        using var lifetime = new WindowWorkLifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = lifetime.RunAsync(async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        var second = lifetime.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lifetime.Dispose();

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(lifetime.IsDisposed);
        Assert.Equal(0, lifetime.ActiveCount);
    }

    [Fact]
    public async Task Work_requested_after_closure_is_not_started()
    {
        var lifetime = new WindowWorkLifetime();
        lifetime.Dispose();
        var called = false;

        await lifetime.RunAsync(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        Assert.False(called);
        Assert.Equal(0, lifetime.ActiveCount);
    }

    [Fact]
    public async Task Ordinary_failures_are_not_hidden()
    {
        using var lifetime = new WindowWorkLifetime();
        var failure = new InvalidOperationException("fixture");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifetime.RunAsync(_ => Task.FromException(failure)));

        Assert.Same(failure, actual);
        Assert.Equal(0, lifetime.ActiveCount);
    }

    [Fact]
    public async Task Cancellation_not_requested_by_the_owner_is_not_hidden()
    {
        using var lifetime = new WindowWorkLifetime();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            lifetime.RunAsync(_ => Task.FromCanceled(new CancellationToken(canceled: true))));

        Assert.False(lifetime.IsDisposed);
        Assert.Equal(0, lifetime.ActiveCount);
    }

    [Fact]
    public async Task Synchronous_delegate_failures_release_the_registration()
    {
        using var lifetime = new WindowWorkLifetime();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifetime.RunAsync(_ => throw new InvalidOperationException("fixture")));

        Assert.Equal(0, lifetime.ActiveCount);
    }

    [Fact]
    public async Task Disposal_is_idempotent_when_a_cancellation_callback_disposes_again()
    {
        var lifetime = new WindowWorkLifetime();
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = lifetime.RunAsync(async ct =>
        {
            using var _ = ct.Register(lifetime.Dispose);
            registered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lifetime.Dispose();
        lifetime.Dispose();

        await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, lifetime.ActiveCount);
    }
}
