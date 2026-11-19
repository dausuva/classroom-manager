// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassroomManager.Client;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-185 over a real UDP socket: only a valid probe is answered, and the answer holds no name.</summary>
[Collection("UdpDiscovery")]
public sealed class AuthenticatedDiscoveryTests
{
    const string LabKey = "synthetic-lab-key-0001";
    const string ClientId = "6532665a4a5e45f4bad18a2d66b44aaa";

    static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    sealed class Rig : IAsyncDisposable
    {
        readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        readonly Task _running;
        public int Port { get; } = FreeUdpPort();
        public byte[] DiscoveryKey { get; } = DiscoveryV2.DeriveKey(Encoding.UTF8.GetBytes(LabKey));

        public Rig(bool allowLegacy = true)
        {
            var config = new ClientConfig { UdpPort = Port, ClientId = ClientId, AllowLegacyDiscovery = allowLegacy };
            var responder = new DiscoveryResponder(config, new KeyStore(LabKey, []));
            _running = Task.Run(() => responder.RunAsync(_stop.Token));
        }

        /// <summary>Sends one packet and returns the first answer within the time, or null for silence. Retries while the responder is binding.</summary>
        public Task<byte[]?> AskAsync(byte[] packet, TimeSpan listenFor, int tries = 1) => AskAsync(() => packet, listenFor, tries);

        /// <summary>
        /// As above, with a new packet for every try: a probe that reached the responder but whose answer was missed is a replay if sent
        /// again, and would be met with silence for ever.
        /// </summary>
        public async Task<byte[]?> AskAsync(Func<byte[]> makePacket, TimeSpan listenFor, int tries = 1)
        {
            for (var attempt = 0; attempt < tries; attempt++)
            {
                var packet = makePacket();
                using var asker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                await asker.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, Port));
                using var stop = new CancellationTokenSource(listenFor);
                try
                {
                    while (true)
                    {
                        try { return (await asker.ReceiveAsync(stop.Token)).Buffer; }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { await Task.Delay(150); break; }   // not listening yet: try again shortly
                    }
                }
                catch (OperationCanceledException) { }
            }
            return null;
        }

        public async Task WaitUntilListeningAsync()
        {
            // The old question is answered while legacy discovery is on; otherwise a valid probe tells us it is up.
            Assert.NotNull(await AskAsync(() => DiscoveryV2.CreateProbe(DiscoveryKey, DiscoveryV2.NonceLength.ToByteArray(), DateTimeOffset.UtcNow), TimeSpan.FromSeconds(1), tries: 30));
            await Task.Delay(1100);   // let that answer leave the one-second window of the rationing
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _running;
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task A_valid_probe_gets_a_51_byte_reply_that_the_host_can_read_and_that_names_nobody()
    {
        await using var rig = new Rig();
        byte[] nonce = [], probe = [];
        var reply = await rig.AskAsync(() =>
        {
            nonce = DiscoveryV2.NonceLength.ToByteArray();
            return probe = DiscoveryV2.CreateProbe(rig.DiscoveryKey, nonce, DateTimeOffset.UtcNow);
        }, TimeSpan.FromSeconds(1), tries: 30);

        Assert.NotNull(reply);
        Assert.Equal(DiscoveryV2.ReplyLength, reply!.Length);
        Assert.True(reply.Length <= probe.Length);
        Assert.True(DiscoveryV2.TryReadReply(reply, rig.DiscoveryKey, nonce, out var read));
        Assert.Equal((5050, Protocol.Minor), (read.TcpPort, read.ProtocolMinor));
        Assert.True(DiscoveryV2.Matches(read, rig.DiscoveryKey, nonce, ClientId, ""));
        Assert.DoesNotContain(Environment.MachineName, Encoding.UTF8.GetString(reply), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_probe_made_with_another_key_an_old_time_or_a_repeat_gets_silence()
    {
        await using var rig = new Rig();
        await rig.WaitUntilListeningAsync();
        var other = DiscoveryV2.DeriveKey(Encoding.UTF8.GetBytes("another-lab-key-9999"));

        Assert.Null(await rig.AskAsync(DiscoveryV2.CreateProbe(other, DiscoveryV2.NonceLength.ToByteArray(), DateTimeOffset.UtcNow), TimeSpan.FromMilliseconds(500)));
        Assert.Null(await rig.AskAsync(DiscoveryV2.CreateProbe(rig.DiscoveryKey, DiscoveryV2.NonceLength.ToByteArray(), DateTimeOffset.UtcNow.AddHours(-1)), TimeSpan.FromMilliseconds(500)));

        var fresh = DiscoveryV2.CreateProbe(rig.DiscoveryKey, DiscoveryV2.NonceLength.ToByteArray(), DateTimeOffset.UtcNow);
        Assert.NotNull(await rig.AskAsync(fresh, TimeSpan.FromSeconds(1)));
        await Task.Delay(400);
        Assert.Null(await rig.AskAsync(fresh, TimeSpan.FromMilliseconds(500)));         // the same probe again: a replay
    }

    [Fact]
    public async Task With_the_old_question_switched_off_it_gets_silence_and_the_probe_still_works()
    {
        await using var rig = new Rig(allowLegacy: false);
        await rig.WaitUntilListeningAsync();

        Assert.Null(await rig.AskAsync(Encoding.UTF8.GetBytes(Protocol.DiscoverMagic), TimeSpan.FromMilliseconds(600)));
        var probe = DiscoveryV2.CreateProbe(rig.DiscoveryKey, DiscoveryV2.NonceLength.ToByteArray(), DateTimeOffset.UtcNow);
        Assert.NotNull(await rig.AskAsync(probe, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task The_old_question_is_still_answered_by_default_so_hosts_not_yet_updated_keep_working()
    {
        await using var rig = new Rig();
        await rig.WaitUntilListeningAsync();

        var reply = await rig.AskAsync(Encoding.UTF8.GetBytes(Protocol.DiscoverMagic), TimeSpan.FromSeconds(1), tries: 10);   // the old question can be repeated safely (a slow CI runner needs more than three goes)
        Assert.NotNull(reply);
        Assert.Contains(ClientId, Encoding.UTF8.GetString(reply!));
    }
}

static class ByteCount
{
    /// <summary>A fresh random array of this many bytes.</summary>
    public static byte[] ToByteArray(this int count) => System.Security.Cryptography.RandomNumberGenerator.GetBytes(count);
}
