// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.BrowserInventory;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class BrowserInventoryLoaderTests
{
    static BrowserInventoryTarget Target(int id = 0, bool user = true, bool browsers = true, bool profiles = true) =>
        new(id, $"PC-{id}", user, browsers, profiles);

    sealed class Connection(Func<string, string, CancellationToken, Task<JsonObject>> read) : IBrowserInventoryConnection
    {
        public List<(string Level, string Command)> Calls { get; } = [];
        public bool Disposed { get; private set; }
        public Task<JsonObject> ReadAsync(string level, string command, CancellationToken cancellation)
        {
            Calls.Add((level, command));
            return read(level, command, cancellation);
        }
        public void Dispose() => Disposed = true;
    }

    static Task<JsonObject> Empty(string level, string command, CancellationToken cancellation)
    {
        var data = new JsonObject();
        if (command == Cmd.BrowserProfilesList) data[BrowserRules.Profiles] = new JsonArray();
        else if (command == Cmd.BrowserList) data[BrowserCatalog.Browsers] = new JsonArray();
        else foreach (var policy in BrowserRules.Policies) data[policy] = "off";
        return Task.FromResult(data);
    }

    static BrowserInventoryLoader Loader(Connection connection) => new((_, _) => Task.FromResult<IBrowserInventoryConnection>(connection));

    [Fact]
    public async Task Successful_empty_profiles_are_distinct_from_unread_profiles()
    {
        var connection = new Connection(Empty);
        var answers = await Loader(connection).ReadAsync([Target()]);
        Assert.Equal(InventoryReadStatus.Success, answers[0].Profiles.Status);
        Assert.Empty(answers[0].Profiles.Value!);
        Assert.Contains("No extra profiles were found", BrowserInventorySummary.Profiles(answers));
        Assert.Empty(BrowserInventorySummary.ReadStatus(answers));
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task Connection_failure_is_unknown_not_empty_and_never_exposes_exception_details()
    {
        var loader = new BrowserInventoryLoader((_, _) => throw new IOException("private-path-and-key"));
        var answers = await loader.ReadAsync([Target()]);
        Assert.Equal(InventoryReadStatus.Unavailable, answers[0].Policies.Status);
        Assert.Equal(InventoryReadStatus.Unavailable, answers[0].Browsers.Status);
        Assert.Equal(InventoryReadStatus.Unavailable, answers[0].Profiles.Status);
        Assert.Contains("state is unknown", BrowserInventorySummary.Profiles(answers));
        var status = BrowserInventorySummary.ReadStatus(answers);
        Assert.Contains("PC-0", status);
        Assert.DoesNotContain("private-path-and-key", status);
        Assert.DoesNotContain("No extra profiles", BrowserInventorySummary.Profiles(answers));
    }

    [Fact]
    public async Task Independent_reads_continue_after_policy_failure()
    {
        var connection = new Connection((level, command, cancellation) => command == Cmd.BrowserPolicyGet
            ? throw new InvalidOperationException("private details") : Empty(level, command, cancellation));
        var answers = await Loader(connection).ReadAsync([Target()]);
        Assert.Equal(InventoryReadStatus.Failed, answers[0].Policies.Status);
        Assert.Equal(InventoryReadStatus.Success, answers[0].Browsers.Status);
        Assert.Equal(InventoryReadStatus.Success, answers[0].Profiles.Status);
        Assert.Equal(3, connection.Calls.Count);
        Assert.True(connection.Disposed);
    }

    [Theory]
    [InlineData(false, true, true, InventoryReadStatus.NoUser)]
    [InlineData(true, false, false, InventoryReadStatus.Unsupported)]
    public async Task Unsupported_commands_and_absent_user_are_not_called(bool user, bool browsers, bool profiles, InventoryReadStatus expected)
    {
        var connection = new Connection(Empty);
        var answers = await Loader(connection).ReadAsync([Target(user: user, browsers: browsers, profiles: profiles)]);
        Assert.Equal(expected, answers[0].Profiles.Status);
        Assert.DoesNotContain(connection.Calls, call => call.Command == Cmd.BrowserProfilesList);
        if (!browsers) Assert.DoesNotContain(connection.Calls, call => call.Command == Cmd.BrowserList);
        else Assert.Contains((Levels.System, Cmd.BrowserList), connection.Calls);
    }

    [Fact]
    public async Task User_browser_error_falls_back_once_to_system_inventory()
    {
        var connection = new Connection((level, command, cancellation) => command == Cmd.BrowserList && level == Levels.User
            ? throw new RemoteException("no helper", new JsonObject()) : Empty(level, command, cancellation));
        var answer = Assert.Single(await Loader(connection).ReadAsync([Target()]));
        Assert.Equal(InventoryReadStatus.Success, answer.Browsers.Status);
        Assert.Equal(new[] { Levels.User, Levels.System }, connection.Calls.Where(call => call.Command == Cmd.BrowserList).Select(call => call.Level));
    }

    [Theory]
    [InlineData(true, InventoryReadStatus.Unsupported)]
    [InlineData(false, InventoryReadStatus.Failed)]
    public async Task Unsupported_or_malformed_profiles_are_not_successful_empty_results(bool unsupported, InventoryReadStatus expected)
    {
        var connection = new Connection((level, command, cancellation) => command == Cmd.BrowserProfilesList && unsupported
            ? throw new UnsupportedFeatureException(command, "1.12", new JsonObject()) : Task.FromResult(new JsonObject()));
        var answers = await Loader(connection).ReadAsync([Target()]);
        Assert.Equal(expected, answers[0].Profiles.Status);
        Assert.Contains("state is unknown", BrowserInventorySummary.Profiles(answers));
    }

    [Fact]
    public async Task Closing_during_a_read_propagates_cancellation_and_disposes_connection()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new Connection((_, _, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(new JsonObject()); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Loader(connection).ReadAsync([Target()], cancellation.Token));
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task Malformed_policy_and_browser_payloads_do_not_reach_summaries_as_success()
    {
        var connection = new Connection((_, _, _) => Task.FromResult(new JsonObject()));
        var answers = await Loader(connection).ReadAsync([Target()]);
        Assert.Equal(InventoryReadStatus.Failed, answers[0].Policies.Status);
        Assert.Equal(InventoryReadStatus.Failed, answers[0].Browsers.Status);
        Assert.Contains("failed", BrowserInventorySummary.ReadStatus(answers));
        Assert.Contains("could not be read", BrowserInventorySummary.Describe(answers.Select(answer => (answer.Target.Computer, answer.Browsers.Value)).ToList()));
    }

    [Fact]
    public async Task Closing_as_connection_opens_does_not_leak_it()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new Connection(Empty);
        var loader = new BrowserInventoryLoader((_, _) => { cancellation.Cancel(); return Task.FromResult<IBrowserInventoryConnection>(connection); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.ReadAsync([Target()], cancellation.Token));
        Assert.True(connection.Disposed);
        Assert.Empty(connection.Calls);
    }

    [Fact]
    public async Task Concurrency_is_bounded_and_results_keep_input_order()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var loader = new BrowserInventoryLoader(async (_, cancellation) =>
        {
            if (Interlocked.Increment(ref count) == 2) entered.SetResult();
            await release.Task.WaitAsync(cancellation);
            return new Connection(Empty);
        });
        var task = loader.ReadAsync(Enumerable.Range(0, 7).Select(id => Target(id)).ToArray(), maxParallel: 2);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref count));
        release.SetResult();
        var answers = await task;
        Assert.Equal(Enumerable.Range(0, 7), answers.Select(answer => answer.Target.Id));
    }

    [Fact]
    public async Task Partial_results_report_only_the_successfully_read_denominator()
    {
        var loader = new BrowserInventoryLoader((target, _) => target.Id == 1 ? throw new TimeoutException() : Task.FromResult<IBrowserInventoryConnection>(new Connection(Empty)));
        var answers = await loader.ReadAsync([Target(0), Target(1)]);
        Assert.Contains("Profiles read from 1 of 2", BrowserInventorySummary.Profiles(answers));
        Assert.Contains("PC-1", BrowserInventorySummary.ReadStatus(answers));
        Assert.DoesNotContain("PC-0", BrowserInventorySummary.ReadStatus(answers));
    }

    [Fact]
    public async Task Wire_adapter_drops_paths_registration_ids_profile_keys_and_unrelated_private_fields()
    {
        var connection = new Connection(async (level, command, cancellation) =>
        {
            var data = await Empty(level, command, cancellation);
            data["private"] = "private-secret";
            if (command == Cmd.BrowserList)
                data[BrowserCatalog.Browsers] = new JsonArray(new BrowserCatalog.Installed("chrome", "Chrome", "private-path", "1", "private-link", "private-file", true).ToJson());
            if (command == Cmd.BrowserProfilesList)
                data[BrowserRules.Profiles] = new JsonArray(new BrowserRules.Profile("Chrome", "private-profile-key", "School", "account", false).ToJson());
            return data;
        });
        var answers = await Loader(connection).ReadAsync([Target()]);
        var json = System.Text.Json.JsonSerializer.Serialize(answers);
        Assert.DoesNotContain("private-", json);
        Assert.Contains("School", json);
        Assert.Contains("account", json);
        Assert.Equal("Chrome", Assert.Single(answers[0].Browsers.Value!.Installed).Name);
    }
}
