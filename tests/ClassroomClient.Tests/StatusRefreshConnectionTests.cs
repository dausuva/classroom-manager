// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

// T-222: the production status adapter through real authenticated loopback frames; no machine commands or real settings.
public sealed class StatusRefreshConnectionTests
{
    const string Key = "synthetic-status-refresh-key";

    sealed class Rig : IAsyncDisposable
    {
        readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly List<Task> _connections = [];
        readonly Task _accepting;
        public ConcurrentQueue<(string Level, string Command)> Calls { get; } = new();
        public CancellationToken Token => _stop.Token;
        public ClientInfo Client => new() { Address = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port };

        public Rig(bool refuseLanguage = false)
        {
            var server = new CommandServer(new ClientConfig { UseTls = false }, new KeyStore(Key, []), (request, _, _, _) =>
            {
                Calls.Enqueue((request.Level, request.Cmd));
                return Task.FromResult(request.Cmd switch
                {
                    Cmd.Ping => Response.Success(new JsonObject { ["machineName"] = "fixture", ["version"] = "1.20", ["userAgent"] = true,
                        ["clientId"] = "fixture-id", ["capabilities"] = new JsonArray(Cmd.WindowsPreferencesGet, Cmd.ProcWindows) }),
                    Cmd.WindowsPreferencesGet => refuseLanguage ? Response.Fail("fixture unavailable") : Response.Success(new JsonObject { ["interfaceLanguage"] = "lt-LT" }),
                    Cmd.ProcWindows => Response.Success(new JsonObject { ["appCount"] = 2, ["windowCount"] = 4 }),
                    _ => Response.Fail("unexpected command"),
                });
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

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _accepting;
            _listener.Stop();
            Task[] connections;
            lock (_connections) connections = [.. _connections];
            await Task.WhenAll(connections);
            _stop.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adapter_routes_only_the_three_status_reads_and_leaves_the_connection_owned_by_core(bool refuseLanguage)
    {
        await using var rig = new Rig(refuseLanguage);
        using var session = await ClientSession.OpenAsync(rig.Client, new HostSettings { Psk = Key, UseTls = false }, rig.Token);
        var connection = new ClientStatusConnection(session);
        var probe = new StatusProbe();
        var ping = await probe.PingAsync(connection, rig.Token);
        Assert.Equal("fixture", ping.Ping.MachineName);
        Assert.Equal("fixture-id", ping.Ping.ClientId);
        Assert.Equal("1.20", ping.Ping.Version);
        Assert.Equal(refuseLanguage ? null : "lt-LT", await probe.InterfaceLanguageAsync(connection, true, true, rig.Token));
        Assert.Equal(new StatusWindows(2, 4), await probe.OpenWindowsAsync(connection, true, rig.Token));
        // The adapter must not close the caller's session after any read (including a refused optional read).
        await session.CallAsync(Cmd.Ping, ct: rig.Token);
        Assert.Equal(new[] { (Levels.System, Cmd.Ping), (Levels.User, Cmd.WindowsPreferencesGet),
            (Levels.User, Cmd.ProcWindows), (Levels.System, Cmd.Ping) }, rig.Calls.ToArray());
    }
}
