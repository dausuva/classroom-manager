// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-190: a Teacher key is held to a number of power commands per minute; the lab key is not.</summary>
public sealed class TeacherCommandLimitsTests
{
    const string Lab = "synthetic-lab-key-0001";
    const string Teacher = "synthetic-teacher-key-0001";

    sealed class Rig : IAsyncDisposable
    {
        readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly List<Task> _connections = [];
        readonly Task _accepting;
        public int Sent;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public CancellationToken Token => _stop.Token;

        public Rig()
        {
            var keys = new KeyStore(Lab, [new ClientAccessKey { Name = "Room 12", Role = "Teacher", Psk = Teacher }]);
            var server = new CommandServer(new ClientConfig { UseTls = false }, keys, (request, _, _, _) =>
            {
                if (request.Cmd == Cmd.PowerAction) Interlocked.Increment(ref Sent);
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

        public Task<ClientSession> Connect(string key) =>
            ClientSession.OpenWithKeyAsync(new ClientInfo { Address = "127.0.0.1", Port = Port }, new HostSettings { UseTls = false }, key, Token);

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

    static Task Power(ClientSession session, CancellationToken ct) =>
        session.CallAsync(Cmd.PowerAction, new System.Text.Json.Nodes.JsonObject { ["action"] = "cancel" }, ct);

    [Fact]
    public async Task A_teacher_key_gets_three_power_commands_a_minute_across_all_its_connections_and_is_then_told_to_wait()
    {
        await using var rig = new Rig();
        using var first = await rig.Connect(Teacher);
        using var second = await rig.Connect(Teacher);

        await Power(first, rig.Token);
        await Power(first, rig.Token);
        await Power(second, rig.Token);                                       // the third, on another connection of the same key
        var ex = await Assert.ThrowsAsync<RemoteException>(() => Power(first, rig.Token));

        Assert.Equal("throttled", ex.Payload["code"]!.GetValue<string>());
        Assert.Equal(3, rig.Sent);                                            // the fourth never reached the handler
        await first.CallAsync(Cmd.Ping, ct: rig.Token);                       // other commands still work on the same connection
    }

    [Fact]
    public async Task The_lab_key_is_not_limited()
    {
        await using var rig = new Rig();
        using var admin = await rig.Connect(Lab);

        for (var i = 0; i < 10; i++) await Power(admin, rig.Token);

        Assert.Equal(10, rig.Sent);
    }
}
