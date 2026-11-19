// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>
/// The same-machine safety guard (README, "Built-in safety"): a Client refuses commands that would shut down, lock or damage the PC when the
/// connection comes from that PC itself. It keeps a teacher from locking themselves out of their own computer.
///
/// None of these tests sends a blocked command to a real handler: if the guard were broken, that would shut down or change the PC running the tests.
/// They test the guard's decision, that the server tells the guard a loopback connection is local, and that README lists what the code blocks.
/// </summary>
[Collection("NetInfo")]
public sealed class SameMachineGuardTests
{
    /// <summary>The commands the guard blocks, written out so that removing one from the guard fails here.</summary>
    static readonly string[] Blocked =
    [
        Cmd.PowerAction, Cmd.LockSet, Cmd.SelfUpdate, Cmd.ClientUninstall, Cmd.SystemRename, Cmd.ProcKill, Cmd.ProcCloseApps, Cmd.BlockAppsSet,
        Cmd.AccountDisable, Cmd.AccountDelete, Cmd.RegistryDeleteKey, Cmd.RegistryImport, Cmd.TasksDelete, Cmd.AutorunRemove, Cmd.UserSignIn,
        Cmd.BrowserDataClear, Cmd.BrowserProfilesRemove, Cmd.WallpaperRecentRemove,
    ];

    static IEnumerable<string> AllCommands() => typeof(Cmd).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => (string)field.GetRawConstantValue()!)
        .Where(command => command.Contains('.') && command == command.ToLowerInvariant());

    static Request Req(string command, JsonObject? args = null) => new() { Cmd = command, Level = Levels.System, Args = args ?? new JsonObject() };

    static Request Power(string? action) => Req(Cmd.PowerAction, action is null ? new JsonObject() : new JsonObject { ["action"] = action });

    [Fact]
    public void Exactly_the_listed_commands_are_blocked_from_the_same_computer()
    {
        var blocked = AllCommands().Where(command => CommandRouter.IsBlockedOnSameMachine(Req(command, new JsonObject { ["action"] = "shutdown" })))
            .OrderBy(command => command, StringComparer.Ordinal).ToList();

        Assert.Equal(Blocked.OrderBy(command => command, StringComparer.Ordinal), blocked);
    }

    [Theory]
    [InlineData("shutdown")]
    [InlineData("restart")]
    [InlineData("logoff")]
    [InlineData("SHUTDOWN")]
    [InlineData("")]
    [InlineData(null)]   // no action at all is refused too: only "cancel" is let through
    public void Every_power_action_but_cancel_is_refused(string? action)
    {
        var refusal = CommandRouter.RefuseOnSameMachine(Power(action), sameMachine: true);

        Assert.NotNull(refusal);
        Assert.False(refusal.Ok);
        Assert.Contains("the host and this client are the same computer", refusal.Error);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("Cancel")]
    public void Cancelling_a_pending_shutdown_still_works_so_one_started_another_way_can_be_stopped(string action)
    {
        Assert.Null(CommandRouter.RefuseOnSameMachine(Power(action), sameMachine: true));
    }

    [Fact]
    public void The_undo_of_a_blocked_action_and_ordinary_commands_go_through()
    {
        foreach (var command in new[] { Cmd.LockClear, Cmd.Ping, Cmd.MessageShow, Cmd.ProcList, Cmd.FsList, Cmd.AccountList, Cmd.AccountCreate, Cmd.RegistrySetValue })
            Assert.Null(CommandRouter.RefuseOnSameMachine(Req(command), sameMachine: true));
    }

    [Fact]
    public void Nothing_is_refused_when_the_connection_comes_from_another_computer()
    {
        foreach (var command in Blocked)
            Assert.Null(CommandRouter.RefuseOnSameMachine(Req(command, new JsonObject { ["action"] = "shutdown" }), sameMachine: false));
    }

    [Fact]
    public void The_refusal_names_the_command()
    {
        Assert.StartsWith($"Refused ({Cmd.ClientUninstall}):", CommandRouter.RefuseOnSameMachine(Req(Cmd.ClientUninstall), sameMachine: true)!.Error);
    }

    [Fact]
    public void Loopback_addresses_in_every_form_count_as_this_computer()
    {
        Assert.True(NetInfo.IsLocalAddress(IPAddress.Loopback));
        Assert.True(NetInfo.IsLocalAddress(IPAddress.Parse("127.0.0.5")));
        Assert.True(NetInfo.IsLocalAddress(IPAddress.IPv6Loopback));
        Assert.True(NetInfo.IsLocalAddress(IPAddress.Loopback.MapToIPv6()));   // ::ffff:127.0.0.1, as a dual-stack socket reports it
    }

    [Fact]
    public void An_address_of_one_of_this_computers_network_cards_counts_as_this_computer()
    {
        var own = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
        if (own is null) return;   // a machine with no network card but loopback (some build agents) has nothing to check here

        Assert.True(NetInfo.IsLocalAddress(own));
        Assert.True(NetInfo.IsLocalAddress(own.MapToIPv6()));
    }

    [Fact]
    public void Another_computers_address_and_no_address_do_not_count_as_this_computer()
    {
        Assert.False(NetInfo.IsLocalAddress(IPAddress.Parse("192.0.2.10")));      // TEST-NET-1, never assigned to a real card
        Assert.False(NetInfo.IsLocalAddress(IPAddress.Parse("2001:db8::10")));    // the IPv6 documentation range
        Assert.False(NetInfo.IsLocalAddress(null));
    }

    [Fact]
    public async Task The_server_tells_the_router_that_a_connection_from_this_computer_is_local()
    {
        // The guard only works if the server passes "same machine" on. A connection to 127.0.0.1 must arrive with it set.
        const string key = "synthetic-same-machine-key";
        bool? seen = null;
        var server = new CommandServer(new ClientConfig { UseTls = false }, new KeyStore(key, []), (_, sameMachine, _, _) =>
        {
            seen = sameMachine;
            return Task.FromResult(Response.Success());
        }, new PreAuthGate(new ConnectionLimits { FailuresBeforeBackoff = 100 }));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accepting = Task.Run(async () =>
            {
                var tcp = await listener.AcceptTcpClientAsync(stop.Token);
                await server.HandleAsync(tcp, stop.Token);
            });
            var info = new ClientInfo { Address = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
            using (var session = await ClientSession.OpenAsync(info, new HostSettings { UseTls = false, Psk = key }, stop.Token))
                await session.CallAsync(Cmd.LockClear, ct: stop.Token);   // harmless; only the flag that reaches the handler is looked at
            Assert.True(seen);
            stop.Cancel();
            try { await accepting; } catch (OperationCanceledException) { }
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void README_lists_every_command_the_guard_blocks()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ClassroomManager.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var readme = File.ReadAllText(Path.Combine(root.FullName, "README.md"));
        var start = readme.IndexOf("## Built-in safety", StringComparison.Ordinal);
        Assert.True(start >= 0, "README has no \"Built-in safety\" section");
        var list = readme[start..readme.IndexOf("Everything else", start, StringComparison.Ordinal)];
        var documented = Regex.Matches(list, "`([a-z]+(?:\\.[a-z\\-]+)+)`").Select(match => match.Groups[1].Value).Where(name => name != Cmd.LockClear).ToHashSet();

        Assert.Equal(Blocked.OrderBy(command => command, StringComparer.Ordinal), documented.OrderBy(command => command, StringComparer.Ordinal));
    }
}
