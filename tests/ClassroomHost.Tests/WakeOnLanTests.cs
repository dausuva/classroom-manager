// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net;
using System.Net.Sockets;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class WakeOnLanTests
{
    [Theory]
    [InlineData("001122aAbBcC")]
    [InlineData("00:11:22:AA:BB:CC")]
    [InlineData("00-11-22-AA-BB-CC")]
    [InlineData("0011.22AA.BBCC")]
    [InlineData(" 00 11 22 AA BB CC ")]
    public void Magic_packet_has_exact_header_and_sixteen_mac_copies(string mac)
    {
        var packet = WakeOnLan.CreatePacket(mac);
        Assert.Equal(102, packet.Length);
        Assert.Equal(Enumerable.Repeat((byte)255, 6), packet.Take(6));
        for (var offset = 6; offset < packet.Length; offset += 6)
            Assert.Equal(Convert.FromHexString("001122AABBCC"), packet.Skip(offset).Take(6));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00:11:22:33:44")]
    [InlineData("00:11:22:33:44:55:66")]
    [InlineData("00:11:22:33:44:5Z")]
    [InlineData("xx00:11:22:33:44:55")]
    [InlineData("00/11/22/33/44/55")]
    public void Invalid_addresses_are_rejected_instead_of_stripping_arbitrary_text(string? mac) =>
        Assert.Throws<ArgumentException>(() => WakeOnLan.CreatePacket(mac));

    [Fact]
    public async Task Sends_exact_packet_to_each_unique_broadcast_on_udp_nine_despite_one_failed_interface()
    {
        var packet = WakeOnLan.CreatePacket("001122334455");
        var subnet = IPAddress.Parse("192.0.2.255");
        var attempts = new List<IPEndPoint>();
        await WakeOnLan.BroadcastAsync(packet, [IPAddress.Broadcast, subnet, subnet], (data, endpoint, _) =>
        {
            Assert.Equal(packet, data.ToArray());
            attempts.Add(endpoint);
            if (endpoint.Address.Equals(IPAddress.Broadcast)) throw new SocketException((int)SocketError.NetworkUnreachable);
            return ValueTask.FromResult(data.Length);
        }, CancellationToken.None);
        Assert.Equal(new[] { new IPEndPoint(IPAddress.Broadcast, 9), new IPEndPoint(subnet, 9) }, attempts);
    }

    [Fact]
    public async Task All_failed_sends_and_an_empty_target_list_cannot_report_success()
    {
        var ex = await Assert.ThrowsAsync<IOException>(() => WakeOnLan.BroadcastAsync(new byte[102],
            [IPAddress.Broadcast], (_, _, _) => throw new SocketException((int)SocketError.NetworkUnreachable), CancellationToken.None));
        Assert.IsType<SocketException>(ex.InnerException);
        await Assert.ThrowsAsync<IOException>(() => WakeOnLan.BroadcastAsync(new byte[102], [],
            (_, _, _) => throw new InvalidOperationException("Must not send"), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_sending()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WakeOnLan.BroadcastAsync(new byte[102],
            [IPAddress.Broadcast], (_, _, _) => throw new InvalidOperationException("Must not send"), stop.Token));
    }

    [Fact]
    public async Task Udp_transport_delivers_the_actual_magic_packet()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var destination = (IPEndPoint)receiver.Client.LocalEndPoint!;
        var packet = WakeOnLan.CreatePacket("001122334455");
        await WakeOnLan.BroadcastAsync(packet, [IPAddress.Loopback],
            (data, _, token) => sender.SendAsync(data, destination, token), limit.Token);
        Assert.Equal(packet, (await receiver.ReceiveAsync(limit.Token)).Buffer);
    }

    static Discovery.NetworkSubnet Subnet(string local, string network, int prefix, string broadcast) =>
        new(network + "/" + prefix, "Ethernet", "Test adapter", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
            IPAddress.Parse(local), IPAddress.Parse(network), prefix, IPAddress.Parse(broadcast));

    [Fact]
    public void Every_network_card_sends_its_own_packet_to_its_broadcast_on_both_ports()
    {
        var routes = WakeOnLan.Routes([Subnet("10.42.1.5", "10.42.1.0", 24, "10.42.1.255"), Subnet("10.42.9.5", "10.42.9.0", 24, "10.42.9.255")], null);
        foreach (var local in new[] { "10.42.1.5", "10.42.9.5" })
            foreach (var port in new[] { 9, 7 })
            {
                var broadcast = local.StartsWith("10.42.1.", StringComparison.Ordinal) ? "10.42.1.255" : "10.42.9.255";
                Assert.Contains(new WakeOnLan.WakeRoute(IPAddress.Parse(local), new IPEndPoint(IPAddress.Parse(broadcast), port)), routes);
                Assert.Contains(new WakeOnLan.WakeRoute(IPAddress.Parse(local), new IPEndPoint(IPAddress.Broadcast, port)), routes);
            }
        Assert.Equal(8, routes.Count);
    }

    [Fact]
    public void The_last_known_address_is_also_sent_to_directly_from_the_card_on_its_network()
    {
        var routes = WakeOnLan.Routes([Subnet("10.42.1.5", "10.42.1.0", 24, "10.42.1.255"), Subnet("10.42.9.5", "10.42.9.0", 24, "10.42.9.255")], " 10.42.9.77 ");
        var direct = routes.Where(route => route.Destination.Address.Equals(IPAddress.Parse("10.42.9.77"))).ToList();
        Assert.Equal([9, 7], direct.Select(route => route.Destination.Port));
        Assert.All(direct, route => Assert.Equal(IPAddress.Parse("10.42.9.5"), route.Source));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("fe80::1")]
    public void An_address_that_cannot_be_woken_directly_adds_no_direct_route(string address)
    {
        var subnets = new[] { Subnet("10.42.1.5", "10.42.1.0", 24, "10.42.1.255") };
        Assert.Equal(WakeOnLan.Routes(subnets, null), WakeOnLan.Routes(subnets, address));
    }

    [Fact]
    public void An_address_on_no_connected_network_is_sent_to_from_every_card_and_no_card_still_broadcasts()
    {
        var subnets = new[] { Subnet("10.42.1.5", "10.42.1.0", 24, "10.42.1.255") };
        Assert.Contains(WakeOnLan.Routes(subnets, "10.77.0.4"), route => route.Destination.Address.Equals(IPAddress.Parse("10.77.0.4")) && route.Source.Equals(subnets[0].LocalAddress));
        var none = WakeOnLan.Routes([], null);
        Assert.Equal([9, 7], none.Select(route => route.Destination.Port));
        Assert.All(none, route => Assert.Equal(IPAddress.Broadcast, route.Destination.Address));
    }

    [Fact]
    public async Task One_failed_route_does_not_stop_the_others_and_all_failing_throws()
    {
        var good = new WakeOnLan.WakeRoute(IPAddress.Loopback, new IPEndPoint(IPAddress.Parse("192.0.2.255"), 9));
        var bad = new WakeOnLan.WakeRoute(IPAddress.Loopback, new IPEndPoint(IPAddress.Broadcast, 9));
        var attempts = new List<WakeOnLan.WakeRoute>();
        await WakeOnLan.SendRoutesAsync(new byte[102], [bad, good, good], (data, route, _) =>
        {
            attempts.Add(route);
            return route == bad ? throw new SocketException((int)SocketError.NetworkUnreachable) : ValueTask.FromResult(data.Length);
        }, CancellationToken.None);
        Assert.Equal([bad, good], attempts);
        await Assert.ThrowsAsync<IOException>(() => WakeOnLan.SendRoutesAsync(new byte[102], [bad],
            (_, _, _) => throw new SocketException((int)SocketError.NetworkUnreachable), CancellationToken.None));
    }
}
