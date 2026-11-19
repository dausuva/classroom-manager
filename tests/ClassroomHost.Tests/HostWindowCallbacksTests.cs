// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.CompilerServices;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

// T-222: lifecycle of legacy process-wide callbacks; serialize with all shared UI-preference tests.
[Collection(UiPreferencesTests.Name)]
public sealed class HostWindowCallbacksTests : IDisposable
{
    readonly Func<StepUpRequest, Task<StepUpAnswer>>? _originalPrompt = StepUp.Prompt;
    readonly Func<IReadOnlyList<ClientInfo>> _originalComputers = ComputerPicker.Computers;
    readonly Func<string, int>? _originalCount = UiPreferences.Instance.CountOpening;
    static readonly StepUpRequest Request = new(Cmd.CmdRun, "run a command", "fixture");

    public void Dispose()
    {
        StepUp.Prompt = _originalPrompt;
        ComputerPicker.Computers = _originalComputers;
        UiPreferences.Instance.CountOpening = _originalCount;
        StepUp.Lock();
    }

    static HostWindowCallbacks Owner(string name, int count = 7) => new(
        _ => Task.FromResult(StepUpAnswer.Allow),
        () => [new ClientInfo { Name = name }], _ => count);

    [Fact]
    public async Task Live_owner_supplies_all_three_callbacks_and_closure_restores_the_baseline()
    {
        using var owner = Owner("first");
        Assert.False(owner.IsDisposed);
        Assert.Equal(StepUpAnswer.Allow, await StepUp.Prompt!(Request));
        Assert.Equal("first", Assert.Single(ComputerPicker.Computers()).Name);
        Assert.Equal(7, UiPreferences.Instance.CountOpening!("window"));

        owner.Dispose();
        owner.Dispose();
        Assert.True(owner.IsDisposed);
        Assert.Same(_originalPrompt, StepUp.Prompt);
        Assert.Same(_originalComputers, ComputerPicker.Computers);
        Assert.Same(_originalCount, UiPreferences.Instance.CountOpening);
    }

    [Fact]
    public async Task Cached_callbacks_after_closure_are_harmless_and_do_not_call_the_closed_owner()
    {
        var calls = 0;
        using var owner = new HostWindowCallbacks(
            _ => { calls++; return Task.FromResult(StepUpAnswer.Allow); },
            () => { calls++; return []; }, _ => { calls++; return 7; });
        var prompt = StepUp.Prompt!;
        var computers = ComputerPicker.Computers;
        var count = UiPreferences.Instance.CountOpening!;
        owner.Dispose();
        Assert.Equal(StepUpAnswer.Deny, await prompt(Request));
        Assert.Empty(computers());
        Assert.Equal(0, count("window"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Closing_the_newest_owner_restores_the_previous_live_owner()
    {
        using var first = Owner("first", 1);
        var firstPrompt = StepUp.Prompt;
        using var second = Owner("second", 2);
        Assert.Equal("second", Assert.Single(ComputerPicker.Computers()).Name);
        second.Dispose();
        Assert.Same(firstPrompt, StepUp.Prompt);
        Assert.Equal("first", Assert.Single(ComputerPicker.Computers()).Name);
        Assert.Equal(1, UiPreferences.Instance.CountOpening!("window"));
    }

    [Fact]
    public void Closing_older_owners_first_does_not_replace_the_newest_or_resurrect_closed_callbacks()
    {
        using var first = Owner("first");
        using var middle = Owner("middle");
        using var last = Owner("last", 3);
        var lastPrompt = StepUp.Prompt;
        first.Dispose();
        middle.Dispose();
        Assert.Same(lastPrompt, StepUp.Prompt);
        Assert.Equal("last", Assert.Single(ComputerPicker.Computers()).Name);
        Assert.Equal(3, UiPreferences.Instance.CountOpening!("window"));
        last.Dispose();
        Assert.Same(_originalPrompt, StepUp.Prompt);
        Assert.Same(_originalComputers, ComputerPicker.Computers);
        Assert.Same(_originalCount, UiPreferences.Instance.CountOpening);
    }

    [Fact]
    public void Disposal_preserves_independently_replaced_slots()
    {
        using var owner = Owner("owned");
        Func<StepUpRequest, Task<StepUpAnswer>> prompt = _ => Task.FromResult(StepUpAnswer.Deny);
        Func<IReadOnlyList<ClientInfo>> computers = () => [];
        Func<string, int> count = _ => 42;
        StepUp.Prompt = prompt;
        ComputerPicker.Computers = computers;
        UiPreferences.Instance.CountOpening = count;
        owner.Dispose();
        Assert.Same(prompt, StepUp.Prompt);
        Assert.Same(computers, ComputerPicker.Computers);
        Assert.Same(count, UiPreferences.Instance.CountOpening);
    }

    [Fact]
    public void An_independently_replaced_slot_does_not_prevent_the_other_slots_from_being_restored()
    {
        using var first = Owner("first", 1);
        using var second = Owner("second", 2);
        Func<string, int> count = _ => 42;
        UiPreferences.Instance.CountOpening = count;
        second.Dispose();
        Assert.Equal("first", Assert.Single(ComputerPicker.Computers()).Name);
        Assert.Same(count, UiPreferences.Instance.CountOpening);
        first.Dispose();
        Assert.Same(_originalPrompt, StepUp.Prompt);
        Assert.Same(_originalComputers, ComputerPicker.Computers);
        Assert.Same(count, UiPreferences.Instance.CountOpening);
    }

    [Fact]
    public void A_new_owner_restores_an_external_callback_that_replaced_an_older_live_owner()
    {
        using var first = Owner("first");
        Func<IReadOnlyList<ClientInfo>> external = () => [new ClientInfo { Name = "external" }];
        ComputerPicker.Computers = external;
        using var second = Owner("second");
        Assert.Equal("second", Assert.Single(ComputerPicker.Computers()).Name);
        second.Dispose();
        Assert.Same(external, ComputerPicker.Computers);
        first.Dispose();
        Assert.Same(external, ComputerPicker.Computers);
    }

    [Fact]
    public async Task Closing_during_a_prompt_denies_its_answer_and_a_waiting_request_without_unlocking()
    {
        StepUp.Lock();
        var answer = new TaskCompletionSource<StepUpAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asks = 0;
        using var owner = new HostWindowCallbacks(_ => { asks++; return answer.Task; }, () => [], _ => 0);
        var active = StepUp.EnsureAsync(Cmd.CmdRun, "first");
        var waiting = StepUp.EnsureAsync(Cmd.CmdRun, "second");
        owner.Dispose();
        answer.SetResult(StepUpAnswer.Allow);
        await Assert.ThrowsAsync<StepUpDeniedException>(() => active.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<StepUpDeniedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, asks);
        Assert.False(StepUp.IsUnlocked);
    }

    sealed class CallbackTarget
    {
        public Task<StepUpAnswer> Prompt(StepUpRequest _) => Task.FromResult(StepUpAnswer.Allow);
        public IReadOnlyList<ClientInfo> Computers() => [];
        public int CountOpening(string _) => 7;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (HostWindowCallbacks Owner, WeakReference Target, Delegate[] Cached) ClosedTarget()
    {
        var target = new CallbackTarget();
        var owner = new HostWindowCallbacks(target.Prompt, target.Computers, target.CountOpening);
        Delegate[] cached = [StepUp.Prompt!, ComputerPicker.Computers, UiPreferences.Instance.CountOpening!];
        owner.Dispose();
        return (owner, new WeakReference(target), cached);
    }

    [Fact]
    public void Disposed_owners_and_cached_wrappers_release_the_original_callback_target()
    {
        var (owner, target, cached) = ClosedTarget();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(target.IsAlive);
        GC.KeepAlive(owner);
        GC.KeepAlive(cached);
    }
}
