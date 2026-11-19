// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// MW-I (inventory only): what the Client's <c>CommandRouter</c> keeps central, so a later constrained handler registry cannot move authorization or routing
/// without these tests saying so. The trace itself is in docs/CLIENT-ROUTER-INVENTORY.md.
/// </summary>
public sealed class ClientRouterInventoryTests
{
    static string Read(params string[] parts) => File.ReadAllText(Path.Combine([StringExtractor.RepositoryRoot(), "src", "ClassroomClient", .. parts]));

    [Fact]
    public void A_request_passes_ping_then_the_role_check_then_the_staging_rules_then_the_same_machine_guard_before_it_is_forwarded_or_handled()
    {
        var router = Read("CommandRouter.cs");
        var start = router.IndexOf("public async Task<Response> HandleAsync(", StringComparison.Ordinal);
        var end = router.IndexOf("bool MayRemoveStaged(", start, StringComparison.Ordinal);
        var handle = router[start..end];
        var previous = -1;
        foreach (var token in new[] { "Cmd.Ping", "CommandAccess.IsAllowed(caller.Role, req.Cmd)", "SleepGuard.Enter(req.Cmd)", "_stagingOwners.MayWrite", "MayRemoveStaged(caller, req)",
                     "RefuseOnSameMachine(req, sameMachine)", "_forwarder.ForwardAsync(req, ct)", "HandleLocalAsync(req, caller, ct)" })
        {
            var index = handle.IndexOf(token, previous + 1, StringComparison.Ordinal);
            Assert.True(index > previous, token);
            previous = index;
        }
    }

    [Fact]
    public void There_are_exactly_two_routers_the_service_with_keys_and_a_forwarder_and_the_helper_with_neither()
    {
        var all = Directory.GetFiles(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomClient"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"new CommandRouter\(|CommandRouter _router = new\(").Select(_ => Path.GetFileName(path)))
            .Order().ToList();
        Assert.Equal(["Program.cs", "UserAgent.cs"], all);
        Assert.Contains("new CommandRouter(config, bridge, blockHandler, keys)", Read("Program.cs"));
        Assert.Contains("new(new ClientConfig(), forwarder: null, BlockHandler.ForHelper())", Read("UserAgent.cs"));
    }

    [Fact]
    public void The_key_handler_exists_only_where_a_key_store_was_given_so_the_helper_cannot_change_keys()
    {
        var router = Read("CommandRouter.cs");
        Assert.Contains("_key = keys is null ? null : new KeyHandler(keys);", router);
        foreach (var command in new[] { "Cmd.KeySet", "Cmd.AccessKeysList", "Cmd.AccessKeysSet", "Cmd.AccessKeysRemove" })
            Assert.Matches($@"case {Regex.Escape(command)}: return _key is null", router);
    }

    [Fact]
    public void Handlers_that_name_a_file_in_the_signed_in_users_folder_are_made_on_first_use_only()
    {
        var router = Read("CommandRouter.cs");
        Assert.Matches(@"Lazy<AppearanceHandler> _appearance = new\(\(\) => new AppearanceHandler\(", router);
        Assert.Contains("Path.Combine(ClientConfig.UserDir, \"appearance-originals.json\")", router);
        Assert.Contains("Lazy<TerminalHandler> _terminal = new();", router);
    }

    [Fact]
    public void Every_command_the_router_runs_is_a_Shared_constant_and_user_level_commands_are_forwarded()
    {
        var router = Read("CommandRouter.cs");
        var local = router[router.IndexOf("async Task<Response> HandleLocalAsync(", StringComparison.Ordinal)..];
        var commands = Regex.Matches(local, @"case Cmd\.(\w+):").Select(match => match.Groups[1].Value).Distinct().ToList();
        Assert.True(commands.Count > 100, commands.Count.ToString());
        // The role table is complete by its own test; here only that the router never dispatches a command Shared does not define as a constant.
        var protocol = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "Shared", "Protocol.cs"));
        foreach (var command in commands)
            Assert.Contains($" {command} =", protocol);
        Assert.Contains("req.Level == Levels.User", router);
        Assert.Contains("Cmd.IsUserOnly(req.Cmd)", router);
    }
}
