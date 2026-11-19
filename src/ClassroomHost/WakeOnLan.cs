// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ClassroomHost.Tests")]

namespace ClassroomManager.Host;

public static class WakeOnLan
{
    /// <summary>The ports a magic packet is sent to: 9 (discard, the usual one) and 7 (echo, which some network cards and routers expect).</summary>
    internal static readonly int[] Ports = [9, 7];

    /// <summary>One way of sending the packet: from which local address (so it leaves through that network card), to where.</summary>
    internal readonly record struct WakeRoute(IPAddress Source, IPEndPoint Destination);

    /// <summary>
    /// Sends a magic packet by UDP, outside the secure channel (a computer that is off has no channel). Each connected network card sends its own
    /// packet from its own address, so the packet cannot leave through the wrong card of a Host that has several (Wi-Fi next to Ethernet, a VPN,
    /// Hyper-V); it goes to the card's directed broadcast, the limited broadcast and, when the computer's last address is known, straight to that
    /// address, on ports 9 and 7. Throws if no local send succeeds. Does not confirm wake.
    /// </summary>
    public static async Task SendAsync(string mac, string? lastAddress, CancellationToken ct = default)
    {
        var packet = CreatePacket(mac);
        var routes = Routes(Discovery.ConnectedSubnets(), lastAddress);
        var senders = new Dictionary<IPAddress, UdpClient>();
        try
        {
            await SendRoutesAsync(packet, routes, async (data, route, token) =>
            {
                if (!senders.TryGetValue(route.Source, out var udp))
                {
                    udp = new UdpClient(new IPEndPoint(route.Source, 0)) { EnableBroadcast = true };
                    senders[route.Source] = udp;
                }
                return await udp.SendAsync(data, route.Destination, token);
            }, ct);
        }
        finally { foreach (var udp in senders.Values) udp.Dispose(); }
    }

    /// <summary>Sends a magic packet to every connected broadcast; see the overload with the last known address.</summary>
    public static Task SendAsync(string mac, CancellationToken ct = default) => SendAsync(mac, null, ct);

    /// <summary>
    /// What to send: for every connected subnet its directed broadcast and the limited broadcast from that card, plus the computer's last
    /// known address (from the card whose subnet holds it, or from any card when it is on another network), on each port. With no connected
    /// subnet at all the system's own choice of card is used, as before.
    /// </summary>
    internal static List<WakeRoute> Routes(IReadOnlyList<Discovery.NetworkSubnet> subnets, string? lastAddress)
    {
        var routes = new List<WakeRoute>();
        void Add(IPAddress source, IPAddress destination)
        {
            foreach (var port in Ports) routes.Add(new WakeRoute(source, new IPEndPoint(destination, port)));
        }
        var unicast = IPAddress.TryParse(lastAddress?.Trim(), out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork
            && !IPAddress.Any.Equals(parsed) && !IPAddress.Broadcast.Equals(parsed) && !IPAddress.IsLoopback(parsed) ? parsed : null;
        foreach (var subnet in subnets)
        {
            Add(subnet.LocalAddress, subnet.BroadcastAddress);
            Add(subnet.LocalAddress, IPAddress.Broadcast);
        }
        if (unicast is not null)
        {
            var onLink = subnets.Where(subnet => subnet.Contains(unicast)).ToList();
            foreach (var subnet in onLink.Count > 0 ? onLink : subnets) Add(subnet.LocalAddress, unicast);
            if (subnets.Count == 0) Add(IPAddress.Any, unicast);
        }
        if (subnets.Count == 0) Add(IPAddress.Any, IPAddress.Broadcast);
        return routes.Distinct().ToList();
    }

    internal static async Task SendRoutesAsync(ReadOnlyMemory<byte> packet, IEnumerable<WakeRoute> routes,
        Func<ReadOnlyMemory<byte>, WakeRoute, CancellationToken, ValueTask<int>> send, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sent = false;
        Exception? lastError = null;
        foreach (var route in routes.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await send(packet, route, ct);
                sent = true;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { lastError = ex; } // Another card may still work.
        }
        if (!sent) throw new IOException("Wake-on-LAN packet could not be sent to any broadcast address.", lastError);
    }

    /// <summary>Builds the standard six-byte header and sixteen copies of the target MAC.</summary>
    public static byte[] CreatePacket(string? mac)
    {
        mac = mac?.Trim() ?? "";
        if (mac.Any(c => !Uri.IsHexDigit(c) && c is not ':' and not '-' and not '.' && !char.IsWhiteSpace(c)))
            throw new ArgumentException("Invalid MAC address. Use 12 hexadecimal digits with optional separators.", nameof(mac));
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) throw new ArgumentException("Unknown MAC address (the computer has not been seen online yet).");
        var macBytes = Convert.FromHexString(hex);

        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 0; i < 16; i++) Buffer.BlockCopy(macBytes, 0, packet, 6 + i * 6, 6);

        return packet;
    }

    internal static async Task BroadcastAsync(ReadOnlyMemory<byte> packet, IEnumerable<IPAddress> targets,
        Func<ReadOnlyMemory<byte>, IPEndPoint, CancellationToken, ValueTask<int>> send, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sent = false;
        SocketException? lastError = null;
        foreach (var target in targets.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await send(packet, new IPEndPoint(target, 9), ct);
                sent = true;
            }
            catch (SocketException ex) { lastError = ex; } // Another interface may still work.
        }
        if (!sent) throw new IOException("Wake-on-LAN packet could not be sent to any broadcast address.", lastError);
    }
}
