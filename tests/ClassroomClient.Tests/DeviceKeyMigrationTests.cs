// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-188: giving a computer its own key, with a fake Client that behaves like the real key.set.</summary>
public sealed class DeviceKeyMigrationTests
{
    const string Master = "k7mq2-xh9vd-3ptwa-e8fzn-4bjrs";
    const string Id17 = "6532665a4a5e45f4bad18a2d66b44aaa";

    sealed class Rig : IAsyncDisposable
    {
        readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly List<Task> _connections = [];
        readonly Task _accepting;
        public KeyStore Keys { get; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public CancellationToken Token => _stop.Token;

        public Rig(KeyStore keys, string clientId = Id17, bool refuseKeySet = false)
        {
            Keys = keys;
            var server = new CommandServer(new ClientConfig { UseTls = false }, keys, (request, _, caller, _) =>
            {
                if (request.Cmd == Cmd.Ping) return Task.FromResult(Response.Success(new JsonObject { ["clientId"] = clientId, ["keyName"] = caller.KeyName }));
                if (request.Cmd == Cmd.KeySet)
                {
                    if (refuseKeySet) return Task.FromResult(Response.Fail("Synthetic refusal"));
                    keys.Set(request.Args["key"]!.GetValue<string>());
                    return Task.FromResult(Response.Success());
                }
                return Task.FromResult(Response.Success());
            }, new PreAuthGate(new ConnectionLimits { FailuresBeforeBackoff = 100 }));
            _listener.Start();
            _accepting = Task.Run(async () =>
            {
                try
                {
                    while (!Token.IsCancellationRequested)
                    {
                        var tcp = await _listener.AcceptTcpClientAsync(Token);
                        lock (_connections) _connections.Add(server.HandleAsync(tcp, Token));
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        public ClientInfo Info() => new() { Address = "127.0.0.1", Port = Port, ClientId = Id17 };

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _accepting;
            _listener.Stop();
            Task[] all;
            lock (_connections) all = [.. _connections];
            await Task.WhenAll(all);
            _stop.Dispose();
        }
    }

    static HostSettings Host() => new() { UseTls = false, Psk = Master };

    [Fact]
    public async Task The_migrated_computer_remembers_its_new_key_id_and_Record_writes_it_into_the_saved_computer()
    {
        await using var rig = new Rig(new KeyStore(Master, []));
        var settings = Host();
        settings.Computers.Add(new SavedComputer { Address = "127.0.0.1", Port = rig.Port, KeyId = SharedKey.Fingerprint(Master) });
        var info = rig.Info();
        using var session = await ClientSession.OpenAsync(info, settings, rig.Token);

        await DeviceKeyMigration.ApplyAsync(info, settings, session, rig.Token);

        var own = SharedKey.Fingerprint(DeviceKeys.Derive(Master, Id17));
        Assert.Equal(own, info.KeyId);
        Assert.Equal(1, DeviceKeyMigration.Record(settings, [info]));
        var saved = Assert.Single(settings.Computers);
        Assert.Equal((own, Id17), (saved.KeyId, saved.ClientId));

        // The next connection starts with the key the computer accepts now, so no refused key is sent first.
        using var again = await ClientSession.OpenAsync(new ClientInfo { Address = "127.0.0.1", Port = rig.Port, ClientId = saved.ClientId, KeyId = saved.KeyId }, settings, rig.Token);
        Assert.Equal(DeviceKeys.Derive(Master, Id17), again.KeyUsed);
    }

    [Fact]
    public async Task Running_it_again_on_a_computer_that_already_has_its_own_key_only_remembers_the_key_id()
    {
        await using var rig = new Rig(new KeyStore(DeviceKeys.Derive(Master, Id17), []));
        var settings = Host();
        settings.Computers.Add(new SavedComputer { Address = "127.0.0.1", Port = rig.Port, KeyId = SharedKey.Fingerprint(Master) });
        var info = rig.Info();
        info.KeyId = SharedKey.Fingerprint(Master);
        using var session = await ClientSession.OpenAsync(info, settings, rig.Token);

        var result = await DeviceKeyMigration.ApplyAsync(info, settings, session, rig.Token);

        Assert.Equal("already has its own key", result);
        Assert.Equal(1, DeviceKeyMigration.Record(settings, [info]));
        Assert.Equal(SharedKey.Fingerprint(DeviceKeys.Derive(Master, Id17)), settings.Computers.Single().KeyId);
    }

    [Fact]
    public void Record_leaves_computers_alone_that_are_not_saved_or_have_no_new_key_id()
    {
        var settings = new HostSettings();
        settings.Computers.Add(new SavedComputer { Address = "10.0.0.5", KeyId = "OLD" });

        Assert.Equal(0, DeviceKeyMigration.Record(settings, [new ClientInfo { Address = "10.0.0.6", KeyId = "NEW" }, new ClientInfo { Address = "10.0.0.5" }]));
        Assert.Equal("OLD", settings.Computers.Single().KeyId);
    }

    [Fact]
    public async Task A_computer_on_the_lab_key_is_given_the_key_derived_for_it_and_stops_accepting_the_lab_key()
    {
        await using var rig = new Rig(new KeyStore(Master, []));
        var settings = Host();
        using var session = await ClientSession.OpenAsync(rig.Info(), settings, rig.Token);

        var result = await DeviceKeyMigration.ApplyAsync(rig.Info(), settings, session, rig.Token);

        Assert.Contains("own key", result);
        Assert.Equal(DeviceKeys.Derive(Master, Id17), rig.Keys.Primary.Psk);
        await Assert.ThrowsAnyAsync<Exception>(() => ClientSession.OpenWithKeyAsync(rig.Info(), settings, Master, rig.Token));   // the lab key no longer opens it
        using var again = await ClientSession.OpenAsync(rig.Info(), settings, rig.Token);                                            // the Host still reaches it
        Assert.Equal(DeviceKeys.Derive(Master, Id17), again.KeyUsed);
    }

    [Fact]
    public async Task A_computer_that_already_has_its_own_key_is_left_alone()
    {
        var own = DeviceKeys.Derive(Master, Id17);
        await using var rig = new Rig(new KeyStore(own, []));
        var settings = Host();
        using var session = await ClientSession.OpenAsync(rig.Info(), settings, rig.Token);

        var result = await DeviceKeyMigration.ApplyAsync(rig.Info(), settings, session, rig.Token);

        Assert.Equal("already has its own key", result);
        Assert.Equal(own, rig.Keys.Primary.Psk);
    }

    [Fact]
    public async Task A_connection_made_with_a_named_key_is_refused_and_nothing_changes()
    {
        var named = new ClientAccessKey { Name = "Room 12", Role = "Admin", Psk = "named-admin-key-0001" };
        await using var rig = new Rig(new KeyStore(Master, [named]));
        var settings = new HostSettings { UseTls = false, Psk = "named-admin-key-0001" };
        using var session = await ClientSession.OpenAsync(rig.Info(), settings, rig.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DeviceKeyMigration.ApplyAsync(rig.Info(), settings, session, rig.Token));

        Assert.Contains("Room 12", ex.Message);
        Assert.Equal(Master, rig.Keys.Primary.Psk);
    }

    [Fact]
    public async Task A_computer_that_refuses_the_key_change_keeps_its_key_and_the_reason_is_passed_on()
    {
        await using var rig = new Rig(new KeyStore(Master, []), refuseKeySet: true);
        var settings = Host();
        using var session = await ClientSession.OpenAsync(rig.Info(), settings, rig.Token);

        await Assert.ThrowsAsync<RemoteException>(() => DeviceKeyMigration.ApplyAsync(rig.Info(), settings, session, rig.Token));

        Assert.Equal(Master, rig.Keys.Primary.Psk);
    }

    [Fact]
    public async Task A_computer_that_does_not_report_its_client_id_is_refused_with_advice()
    {
        await using var rig = new Rig(new KeyStore(Master, []), clientId: "");
        var settings = Host();
        using var session = await ClientSession.OpenAsync(rig.Info(), settings, rig.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DeviceKeyMigration.ApplyAsync(rig.Info(), settings, session, rig.Token));

        Assert.Contains("client id", ex.Message);
        Assert.Equal(Master, rig.Keys.Primary.Psk);
    }

    [Fact]
    public void The_command_needs_a_target_and_an_explicit_yes()
    {
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["derive-keys", "--all"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["derive-keys", "--yes"]));
        var options = CliParser.Parse(["derive-keys", "--computer", "410-17", "--yes"]);
        Assert.Equal(("derive-keys", true), (options.Command, options.Yes));
        Assert.Contains("derive-keys", CliParser.Usage);
    }
}
