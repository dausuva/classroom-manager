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

/// <summary>T-183 over real loopback sockets: the limits before authentication, seen from outside the Client.</summary>
public sealed class PreAuthLimitsTests
{
    const string Key = "synthetic-primary-key";

    sealed class Rig : IAsyncDisposable
    {
        readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly List<Task> _connections = [];
        readonly Task _accepting;
        public PreAuthGate Gate { get; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public CancellationToken Token => _stop.Token;

        public Rig(ConnectionLimits limits, TimeProvider? time = null)
        {
            Gate = new PreAuthGate(limits, time);
            var server = new CommandServer(new ClientConfig { UseTls = false }, new KeyStore(Key, []),
                (_, _, _, _) => Task.FromResult(Response.Success()), Gate);
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

    [Fact]
    public async Task Hundreds_of_silent_connections_from_one_address_do_not_keep_the_teacher_out()
    {
        await using var rig = new Rig(new ConnectionLimits { SetupDeadline = TimeSpan.FromSeconds(3) });
        var attackers = new List<TcpClient>();
        try
        {
            // 127.0.0.2 is another source address for the server; 127.0.0.1 is the teacher.
            for (var i = 0; i < 500; i++)
            {
                var attacker = new TcpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
                attackers.Add(attacker);
                try { await attacker.ConnectAsync(IPAddress.Loopback, rig.Port, rig.Token); }
                catch (SocketException) { }
            }
            await Task.Delay(300);
            Assert.True(rig.Gate.Pending <= rig.Gate.Limits.MaxPendingPerSource, $"{rig.Gate.Pending} connections in setup");

            var started = DateTime.UtcNow;
            using var session = await rig.Connect(Key);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "the teacher waited too long");
        }
        finally { foreach (var attacker in attackers) attacker.Dispose(); }
    }

    [Fact]
    public async Task Silent_connections_are_closed_at_the_setup_deadline_and_freed()
    {
        await using var rig = new Rig(new ConnectionLimits { SetupDeadline = TimeSpan.FromMilliseconds(400) });
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, rig.Port, rig.Token);
        await Eventually.WaitAsync(() => rig.Gate.Pending == 1, "the silent connection is in setup");

        await Eventually.WaitAsync(() => rig.Gate.Pending == 0, "the server closes it at the 400 ms deadline", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task An_address_that_keeps_sending_the_wrong_key_waits_and_then_may_try_again()
    {
        // The wait is measured on a test clock, so the test moves time on instead of sleeping through it.
        var clock = new TestClock();
        await using var rig = new Rig(new ConnectionLimits { FailuresBeforeBackoff = 2, FirstBackoff = TimeSpan.FromSeconds(3) }, clock);

        for (var i = 0; i < 3; i++) await Assert.ThrowsAnyAsync<Exception>(() => rig.Connect("a-wrong-key"));
        // The server records a failure after the client has seen the refusal: wait until it has finished with every connection.
        await Eventually.WaitAsync(() => rig.Gate.Pending == 0, "the server has recorded the three wrong keys");

        await Assert.ThrowsAnyAsync<Exception>(() => rig.Connect(Key));   // dropped, even with the right key
        await Eventually.WaitAsync(() => rig.Gate.Pending == 0, "the server has dropped that connection");
        clock.Advance(TimeSpan.FromSeconds(3.2));
        using var session = await rig.Connect(Key);                      // the wait is over; success forgives the failures
        Assert.NotNull(session);
    }

    [Fact]
    public async Task A_connection_that_asks_too_fast_is_told_to_wait_and_keeps_its_connection()
    {
        await using var rig = new Rig(new ConnectionLimits { RequestBurst = 3, RequestsPerSecond = 1 });
        using var session = await rig.Connect(Key);

        var refused = 0;
        for (var i = 0; i < 8; i++)
        {
            try { await session.CallAsync(Cmd.Ping, ct: rig.Token); }
            catch (RemoteException ex) when (ex.Payload["code"]?.GetValue<string>() == "throttled") { refused++; }
        }

        Assert.InRange(refused, 4, 5);                       // 3 in the burst plus what one second or so earned
        await Task.Delay(1200);
        await session.CallAsync(Cmd.Ping, ct: rig.Token);    // the same connection works again
    }

    [Fact]
    public async Task One_key_cannot_hold_more_sessions_than_the_limit()
    {
        await using var rig = new Rig(new ConnectionLimits { MaxSessionsPerKey = 2 });
        using var first = await rig.Connect(Key);
        using var second = await rig.Connect(Key);
        await first.CallAsync(Cmd.Ping, ct: rig.Token);
        await second.CallAsync(Cmd.Ping, ct: rig.Token);

        using var third = await rig.Connect(Key);
        var ex = await Assert.ThrowsAsync<RemoteException>(() => third.CallAsync(Cmd.Ping, ct: rig.Token));
        Assert.Equal("throttled", ex.Payload["code"]?.GetValue<string>());

        first.Dispose();                                     // a slot comes back once the server has seen the close
        using var fourth = await Eventually.SucceedsAsync(async () =>
        {
            var session = await rig.Connect(Key);
            try { await session.CallAsync(Cmd.Ping, ct: rig.Token); return session; }
            catch { session.Dispose(); throw; }
        }, "the closed session's slot is free again");
    }

    [Fact]
    public async Task Connecting_and_closing_without_a_word_is_not_a_wrong_key_and_never_makes_the_teacher_wait()
    {
        // A port check, a Classroom Health run (it opens and closes a connection) or a monitoring tool does exactly this.
        await using var rig = new Rig(new ConnectionLimits { FailuresBeforeBackoff = 2, FirstBackoff = TimeSpan.FromSeconds(30) });
        for (var i = 0; i < 10; i++)
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, rig.Port, rig.Token);
        }
        await Eventually.WaitAsync(() => rig.Gate.Pending == 0, "the server has seen every probe close");

        using var session = await rig.Connect(Key);                // would be dropped for 30 seconds if the closes had counted

        Assert.NotNull(session);
    }
}
