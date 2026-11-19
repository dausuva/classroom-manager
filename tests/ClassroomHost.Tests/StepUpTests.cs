// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Reflection;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-190: the Host asks once before a dangerous Admin action and then allows them for ten minutes.</summary>
[Collection("StepUp")]
public sealed class StepUpTests : IDisposable
{
    DateTime _now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    public StepUpTests()
    {
        StepUp.Lock();
        StepUp.Prompt = null;
        StepUp.Now = () => _now;
    }

    public void Dispose()
    {
        StepUp.Lock();
        StepUp.Prompt = null;
        StepUp.Now = () => DateTime.UtcNow;
    }

    static IEnumerable<string> AllCommands() => typeof(Cmd).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => (string)field.GetRawConstantValue()!);

    [Fact]
    public void Every_dangerous_command_is_a_real_command_that_needs_an_Admin_key()
    {
        var all = AllCommands().ToHashSet();
        foreach (var command in StepUp.Dangerous.Keys)
        {
            Assert.Contains(command, all);
            Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(command));
        }
    }

    [Fact]
    public void Every_command_that_needs_an_Admin_key_is_either_dangerous_or_listed_as_routine()
    {
        var unclassified = AllCommands()
            .Where(command => CommandAccess.RequiredRole(command) == OperatorRole.Admin)
            .Where(command => !StepUp.Dangerous.ContainsKey(command) && !StepUpRoutine.Commands.Contains(command))
            .OrderBy(command => command, StringComparer.Ordinal).ToList();

        Assert.True(unclassified.Count == 0, "Decide for each: dangerous (StepUp.Dangerous) or routine (StepUpRoutine): " + string.Join(", ", unclassified));
    }

    [Fact]
    public void A_command_is_never_both_dangerous_and_routine()
    {
        Assert.DoesNotContain(StepUpRoutine.Commands, StepUp.Dangerous.ContainsKey);
    }

    [Fact]
    public async Task Where_nobody_can_be_asked_nothing_is_asked()
    {
        await StepUp.EnsureAsync(Cmd.CmdRun, "410-01");   // no prompt is set: the command line, the scheduler and the Health tools
    }

    [Fact]
    public async Task An_ordinary_command_is_not_asked_about()
    {
        var asked = 0;
        StepUp.Prompt = _ => { asked++; return Task.FromResult(StepUpAnswer.Allow); };

        await StepUp.EnsureAsync(Cmd.Ping, "410-01");
        await StepUp.EnsureAsync(Cmd.LockSet, "410-01");

        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task Allowing_once_covers_ten_minutes_and_a_second_question_follows_after_that()
    {
        var asked = new List<StepUpRequest>();
        StepUp.Prompt = request => { asked.Add(request); return Task.FromResult(StepUpAnswer.Allow); };

        await StepUp.EnsureAsync(Cmd.CmdRun, "410-01");
        _now += TimeSpan.FromMinutes(9);
        await StepUp.EnsureAsync(Cmd.RegistryDeleteKey, "410-02");
        Assert.Single(asked);
        Assert.Equal(("run a command", "410-01"), (asked[0].Action, asked[0].Computer));

        _now += TimeSpan.FromMinutes(2);
        await StepUp.EnsureAsync(Cmd.AccountDelete, "410-03");
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public async Task A_refusal_stops_the_command_and_the_next_one_asks_again()
    {
        var asked = 0;
        StepUp.Prompt = _ => { asked++; return Task.FromResult(StepUpAnswer.Deny); };

        var denied = await Assert.ThrowsAsync<StepUpDeniedException>(() => StepUp.EnsureAsync(Cmd.ClientUninstall, "410-01"));
        await Assert.ThrowsAsync<StepUpDeniedException>(() => StepUp.EnsureAsync(Cmd.ClientUninstall, "410-02"));

        Assert.Contains("remove the Client from the computer", denied.Message);
        Assert.Equal(2, asked);
        Assert.False(StepUp.IsUnlocked);
    }

    [Fact]
    public async Task Many_computers_at_once_ask_one_question()
    {
        var asked = 0;
        StepUp.Prompt = async _ => { Interlocked.Increment(ref asked); await Task.Delay(100); return StepUpAnswer.Allow; };

        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => StepUp.EnsureAsync(Cmd.CmdRun, $"410-{i:00}")));

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task A_confirmed_key_change_allows_the_next_ten_minutes_without_a_second_question()
    {
        StepUp.Prompt = _ => throw new InvalidOperationException("must not be asked");

        StepUp.Grant();
        await StepUp.EnsureAsync(Cmd.KeySet, "410-01");
    }
}
