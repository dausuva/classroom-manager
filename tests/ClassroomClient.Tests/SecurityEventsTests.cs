// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-191: the Client's security event log, on scratch files only.</summary>
public sealed class SecurityEventsTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "classroom-events-" + Guid.NewGuid().ToString("N"));
    string LogPath => Path.Combine(_folder, "security-events.log");

    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    sealed class FakeClock : TimeProvider
    {
        DateTimeOffset _now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void Events_are_written_as_a_verifiable_chain_and_counted()
    {
        var log = new SecurityEvents(LogPath, new FakeClock());

        log.Write("handshake-failed", ("source", "10.42.4.9"), ("reason", "Bad pre-shared key from peer."));
        log.Write("throttled", ("source", "10.42.4.9"));
        log.Write("handshake-failed", ("source", "10.42.4.10"));

        var result = SecurityEventChain.Verify(File.ReadLines(LogPath));
        Assert.True(result.Ok);
        Assert.Equal(3, result.Count);
        var snapshot = log.Snapshot();
        Assert.Equal(2, snapshot["counts"]!["handshake-failed"]!.GetValue<long>());
        Assert.Equal(1, snapshot["counts"]!["throttled"]!.GetValue<long>());
        Assert.Equal((3L, result.LastHash), (snapshot["lastSeq"]!.GetValue<long>(), snapshot["lastHash"]!.GetValue<string>()));
    }

    [Fact]
    public void A_new_start_continues_the_chain_where_the_file_ended()
    {
        var first = new SecurityEvents(LogPath, new FakeClock());
        first.Write("service-start");
        first.Write("service-start");

        var second = new SecurityEvents(LogPath, new FakeClock());
        second.Write("service-start");

        var result = SecurityEventChain.Verify(File.ReadLines(LogPath));
        Assert.True(result.Ok);
        Assert.Equal((3, 3L), (result.Count, result.LastSeq));
    }

    [Fact]
    public void A_flood_of_events_is_counted_all_but_written_at_a_limited_rate_with_one_line_saying_how_many_were_left_out()
    {
        var clock = new FakeClock();
        var log = new SecurityEvents(LogPath, clock, burst: 5, perSecond: 1);

        for (var i = 0; i < 100; i++) log.Write("handshake-failed", ("source", "10.0.0." + i));

        Assert.Equal(100, log.Snapshot()["counts"]!["handshake-failed"]!.GetValue<long>());     // every one is counted
        Assert.Equal(5, File.ReadLines(LogPath).Count());                                       // five fit the burst

        clock.Advance(TimeSpan.FromSeconds(5));
        log.Write("throttled");
        var lines = File.ReadLines(LogPath).ToList();
        Assert.Contains("events-suppressed", lines[^2]);
        Assert.Contains("\"count\":\"95\"", lines[^2]);
        Assert.True(SecurityEventChain.Verify(lines).Ok);
    }

    [Fact]
    public void The_file_is_rotated_at_its_size_limit_and_the_chain_continues_across_the_two_files()
    {
        var log = new SecurityEvents(LogPath, new FakeClock(), maxBytes: 600, burst: 1000, perSecond: 1000);

        for (var i = 0; i < 20; i++) log.Write("handshake-failed", ("source", "10.42.4." + i));

        Assert.True(File.Exists(LogPath + ".1"));
        var old = File.ReadLines(LogPath + ".1").ToList();
        var current = File.ReadLines(LogPath).ToList();
        var oldResult = SecurityEventChain.Verify(old);
        Assert.True(oldResult.Ok);
        Assert.True(SecurityEventChain.Verify(current, oldResult.LastHash).Ok);                 // the new file starts where the old one ended
    }

    [Fact]
    public void A_file_that_was_edited_is_noticed_at_the_next_start_and_the_new_events_carry_on_after_the_last_good_line()
    {
        var log = new SecurityEvents(LogPath, new FakeClock());
        for (var i = 0; i < 4; i++) log.Write("handshake-failed", ("source", "10.42.4." + i));
        File.WriteAllText(LogPath, File.ReadAllText(LogPath).Replace("10.42.4.2", "10.42.4.77"));

        var restarted = new SecurityEvents(LogPath, new FakeClock());

        Assert.Equal(1, restarted.Snapshot()["counts"]!["log-chain-broken"]!.GetValue<long>());
        Assert.False(SecurityEventChain.Verify(File.ReadLines(LogPath)).Ok);                    // the break stays visible
    }

    [Fact]
    public void Write_never_throws_even_when_the_folder_cannot_be_written()
    {
        var blocked = Path.Combine(_folder, "a-file-not-a-folder");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(blocked, "x");
        var log = new SecurityEvents(Path.Combine(blocked, "security-events.log"), new FakeClock());

        log.Write("service-start");                                                              // would need a folder where a file is

        Assert.Equal(1, log.Snapshot()["counts"]!["service-start"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_refused_handshake_over_a_real_socket_is_logged_with_its_source_and_never_with_the_key()
    {
        const string RealKey = "the-real-lab-key-0001";
        const string WrongKey = "a-wrong-key-that-must-not-be-logged";
        var log = new SecurityEvents(LogPath);
        // The log is given to this server alone: the static one would also collect what other tests' servers write at the same time.
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = new CommandServer(new ClientConfig { UseTls = false }, new KeyStore(RealKey, []),
                (_, _, _, _) => Task.FromResult(Response.Success()), new PreAuthGate(), log);
            var accepting = Task.Run(async () => await server.HandleAsync(await listener.AcceptTcpClientAsync(stop.Token), stop.Token));

            await Assert.ThrowsAnyAsync<Exception>(() => ClientSession.OpenWithKeyAsync(
                new ClientInfo { Address = "127.0.0.1", Port = port }, new HostSettings { UseTls = false }, WrongKey, stop.Token));
            await accepting;
            listener.Stop();
        }

        var text = File.ReadAllText(LogPath);
        Assert.Contains("handshake-failed", text);
        Assert.Contains("127.0.0.1", text);
        Assert.DoesNotContain(WrongKey, text);
        Assert.DoesNotContain(RealKey, text);
        Assert.True(SecurityEventChain.Verify(File.ReadLines(LogPath)).Ok);
    }
}
