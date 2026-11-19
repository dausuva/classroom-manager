// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>After every key of the Host was refused, the Host stops trying that computer for a while and says why.</summary>
[Collection("KeyRefusalPause")]
public sealed class KeyRefusalPauseTests : IDisposable
{
    const string Right = "k7mq2-xh9vd-3ptwa-e8fzn-4bjrs";
    const string Wrong = "w4xq2-xh9vd-3ptwa-e8fzn-4bjrs";

    DateTime _now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    public KeyRefusalPauseTests()
    {
        KeyRefusalPause.ClearAll();
        KeyRefusalPause.Now = () => _now;
    }

    public void Dispose()
    {
        KeyRefusalPause.ClearAll();
        KeyRefusalPause.Now = () => DateTime.UtcNow;
    }

    static (TcpListener Listener, CancellationTokenSource Stop, Task Accepting, List<int> Connections) Fake(string key)
    {
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var connections = new List<int>();
        var server = new CommandServer(new ClientConfig { UseTls = false }, new KeyStore(key, []),
            (request, _, _, _) => Task.FromResult(Response.Success()),
            new PreAuthGate(new ConnectionLimits { FailuresBeforeBackoff = 100 }));
        listener.Start();
        var tasks = new List<Task>();
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var tcp = await listener.AcceptTcpClientAsync(stop.Token);
                    lock (connections) connections.Add(1);
                    tasks.Add(server.HandleAsync(tcp, stop.Token));
                }
            }
            catch (OperationCanceledException) { }
        });
        return (listener, stop, accepting, connections);
    }

    [Fact]
    public async Task A_computer_that_refused_every_key_is_not_tried_again_until_the_pause_ends()
    {
        var (listener, stop, accepting, connections) = Fake(Right);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var settings = new HostSettings { UseTls = false, Psk = Wrong };
            var info = new ClientInfo { Address = "127.0.0.1", Port = port };

            await Assert.ThrowsAsync<AuthException>(() => ClientSession.OpenAsync(info, settings, stop.Token));
            int after;
            lock (connections) after = connections.Count;
            Assert.NotNull(KeyRefusalPause.EndsAt(info));

            var paused = await Assert.ThrowsAsync<AuthException>(() => ClientSession.OpenAsync(info, settings, stop.Token));
            Assert.True(KeyRefusalPause.IsPauseMessage(paused.Message));
            Assert.Contains("5 more minute(s)", paused.Message);
            lock (connections) Assert.Equal(after, connections.Count);   // nothing was sent to the computer

            _now += KeyRefusalPause.Length + TimeSpan.FromSeconds(1);
            Assert.Null(KeyRefusalPause.EndsAt(info));
            settings.Psk = Right;
            using var session = await ClientSession.OpenAsync(info, settings, stop.Token);
            Assert.Equal(Right, session.KeyUsed);
        }
        finally { stop.Cancel(); await accepting; listener.Stop(); }
    }

    [Fact]
    public async Task Changing_the_lab_keys_or_connecting_ends_the_pause()
    {
        var (listener, stop, accepting, _) = Fake(Right);
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var settings = new HostSettings { UseTls = false, Psk = Right };
            var info = new ClientInfo { Address = "127.0.0.1", Port = port };

            KeyRefusalPause.Start(info);
            KeyRefusalPause.ClearAll();                       // what saving the lab keys does
            using (var session = await ClientSession.OpenAsync(info, settings, stop.Token)) Assert.Equal(Right, session.KeyUsed);

            KeyRefusalPause.Start(info);
            KeyRefusalPause.Clear(info);
            Assert.Null(KeyRefusalPause.EndsAt(info));
        }
        finally { stop.Cancel(); await accepting; listener.Stop(); }
    }

    [Fact]
    public async Task A_computer_that_is_off_is_not_paused()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var info = new ClientInfo { Address = "127.0.0.1", Port = port };

        await Assert.ThrowsAnyAsync<Exception>(() => ClientSession.OpenAsync(info, new HostSettings { UseTls = false, Psk = Right }));

        Assert.Null(KeyRefusalPause.EndsAt(info));
    }
}
