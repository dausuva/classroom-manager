// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public static class Discovery
{
    public sealed record Found(IPAddress Address, DiscoveryReply Info);

    public sealed record NetworkSubnet(
        string Key,
        string InterfaceName,
        string InterfaceDescription,
        NetworkInterfaceType InterfaceType,
        IPAddress LocalAddress,
        IPAddress NetworkAddress,
        int PrefixLength,
        IPAddress BroadcastAddress)
    {
        public string DisplayName => $"{NetworkAddress}/{PrefixLength} - {InterfaceName} ({LocalAddress})";

        public bool Contains(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            var candidate = address.GetAddressBytes();
            var network = NetworkAddress.GetAddressBytes();
            var wholeBytes = PrefixLength / 8;
            var remainingBits = PrefixLength % 8;
            for (var i = 0; i < wholeBytes; i++)
                if (candidate[i] != network[i]) return false;
            if (remainingBits == 0) return true;
            var mask = (byte)(0xFF << (8 - remainingBits));
            return (candidate[wholeBytes] & mask) == (network[wholeBytes] & mask);
        }
    }

    public sealed record SubnetSelection(NetworkSubnet? Subnet, bool ConfiguredSubnetUnavailable);

    /// <summary>
    /// Returns usable connected IPv4 subnets. Physical Ethernet and Wi-Fi adapters sort before
    /// VPN/virtual adapters so automatic discovery defaults to the classroom LAN.
    /// </summary>
    public static IReadOnlyList<NetworkSubnet> ConnectedSubnets() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                          nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(NetworkSubnets)
            .GroupBy(subnet => subnet.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(DefaultRank).ThenBy(x => x.InterfaceName, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(DefaultRank)
            .ThenBy(x => x.NetworkAddress.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>An empty configured key means automatic LAN selection.</summary>
    public static SubnetSelection SelectSubnet(string configuredKey)
    {
        var connected = ConnectedSubnets();
        if (configuredKey.Length > 0)
        {
            var configured = connected.FirstOrDefault(x =>
                x.Key.Equals(configuredKey, StringComparison.OrdinalIgnoreCase));
            if (configured is not null) return new SubnetSelection(configured, false);
        }

        return new SubnetSelection(connected.FirstOrDefault(), configuredKey.Length > 0);
    }

    /// <summary>
    /// Wake-on-LAN still broadcasts on every connected subnet because a sleeping computer cannot
    /// answer discovery and may be reached through a different remembered network.
    /// </summary>
    internal static IEnumerable<IPAddress> BroadcastTargets()
    {
        yield return IPAddress.Broadcast;
        foreach (var target in ConnectedSubnets().Select(x => x.BroadcastAddress).Distinct())
            yield return target;
    }

    /// <summary>One authenticated probe that was sent: its nonce and the discovery key it was made with.</summary>
    public sealed record SentProbe(byte[] Nonce, byte[] DiscoveryKey);

    /// <summary>The authenticated probes for a scan: one per lab key of this Host, each with a fresh nonce.</summary>
    public static List<(SentProbe Sent, byte[] Packet)> MakeProbes(HostSettings settings, DateTimeOffset now) =>
        settings.CurrentHostKeys().Select(key => System.Text.Encoding.UTF8.GetBytes(key.Psk)).DistinctBy(Convert.ToHexString)
            .Select(labKey =>
            {
                var discoveryKey = DiscoveryV2.DeriveKey(labKey);
                var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(DiscoveryV2.NonceLength);
                return (new SentProbe(nonce, discoveryKey), DiscoveryV2.CreateProbe(discoveryKey, nonce, now));
            }).ToList();

    /// <summary>
    /// Reads a reply to an authenticated probe. It carries only tags, so a computer is recognised by recomputing them for the saved
    /// computers (client id or card address); a reply that matches none is a computer this Host has not enrolled and is ignored here,
    /// the old question finds it.
    /// </summary>
    public static DiscoveryReply? ReadAuthenticated(byte[] packet, IReadOnlyList<SentProbe> sent, IReadOnlyList<SavedComputer> computers)
    {
        foreach (var probe in sent)
        {
            if (!DiscoveryV2.TryReadReply(packet, probe.DiscoveryKey, probe.Nonce, out var reply)) continue;
            var known = computers.FirstOrDefault(computer => DiscoveryV2.Matches(reply, probe.DiscoveryKey, probe.Nonce, computer.ClientId, computer.Mac));
            if (known is null) return null;
            return new DiscoveryReply
            {
                MachineName = known.Name, ClientId = known.ClientId, Mac = known.Mac, TcpPort = reply.TcpPort, Version = $"{Protocol.Major}.{reply.ProtocolMinor}",
            };
        }
        return null;
    }

    /// <summary>Reads an enrolment reply against the probes that were sent; null when it is not an answer to one of them under a key of this Host.</summary>
    public static DiscoveryV2Enrolment? ReadEnrolment(byte[] packet, IReadOnlyList<SentProbe> sent)
    {
        foreach (var probe in sent)
            if (DiscoveryV2.TryReadEnrolmentReply(packet, probe.DiscoveryKey, probe.Nonce, out var enrolment)) return enrolment;
        return null;
    }

    /// <summary>Broadcasts on only the selected subnet and collects replies for a few seconds.</summary>
    public static async Task<List<Found>> ScanAsync(
        HostSettings settings, NetworkSubnet? subnet, TimeSpan window, CancellationToken ct, List<EnrolmentOffer>? offers = null)
    {
        var found = new Dictionary<string, Found>();

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(subnet?.LocalAddress ?? IPAddress.Any, 0));

        var payload = Encoding.UTF8.GetBytes(Protocol.DiscoverMagic);
        var target = subnet?.BroadcastAddress ?? IPAddress.Broadcast;
        try { await udp.SendAsync(payload, new IPEndPoint(target, settings.UdpPort), ct); }
        catch (SocketException) { /* selected interface may have gone down */ }

        // The authenticated probe too (protocol 1.18): a Client that understands it answers with tags only. An older Client ignores it.
        var probes = MakeProbes(settings, DateTimeOffset.UtcNow);
        foreach (var (_, packet) in probes)
        {
            try { await udp.SendAsync(packet, new IPEndPoint(target, settings.UdpPort), ct); }
            catch (SocketException) { }
        }
        var sent = probes.Select(probe => probe.Sent).ToList();
        var authenticated = new Dictionary<string, Found>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(window);
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(cts.Token);
                if (DiscoveryV2.LooksLikeEnrolmentReply(result.Buffer))
                {
                    // A newly installed computer announcing its certificate: kept for the caller, which decides (AutoEnrol).
                    if (ReadEnrolment(result.Buffer, sent) is { } offer)
                        offers?.Add(new EnrolmentOffer(result.RemoteEndPoint.Address, offer.TcpPort, offer.ProtocolMinor, Convert.ToHexString(offer.CertificateSha256)));
                    continue;
                }
                if (DiscoveryV2.LooksLikeReply(result.Buffer))
                {
                    if (ReadAuthenticated(result.Buffer, sent, settings.Computers) is { } known)
                        authenticated[result.RemoteEndPoint.Address.ToString()] = new Found(result.RemoteEndPoint.Address, known);
                    continue;
                }
                try
                {
                    var info = JsonSerializer.Deserialize<DiscoveryReply>(result.Buffer, Protocol.Json);
                    if (info is not null)
                        found[result.RemoteEndPoint.Address.ToString()] = new Found(result.RemoteEndPoint.Address, info);
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) { /* window elapsed */ }
        // Windows can surface an ICMP "port unreachable" from an earlier send as a SocketException
        // on the next receive (WSAECONNRESET). Keep whatever replies arrived instead of letting it
        // escape into the UI and crash the scan.
        catch (SocketException) { }

        // What the old question did not find, the authenticated one did (for example when the old answer is switched off on the Client).
        foreach (var (address, known) in authenticated)
            found.TryAdd(address, known);
        return found.Values.ToList();
    }

    static IEnumerable<NetworkSubnet> NetworkSubnets(NetworkInterface nic)
    {
        foreach (var ua in nic.GetIPProperties().UnicastAddresses)
        {
            if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null ||
                !TryPrefixLength(ua.IPv4Mask, out var prefix) || prefix is < 1 or > 30) continue;

            var ip = ua.Address.GetAddressBytes();
            if (ip[0] is 0 or >= 224 || ip[0] == 127) continue;

            var mask = ua.IPv4Mask.GetAddressBytes();
            var network = new byte[4];
            var broadcast = new byte[4];
            for (var i = 0; i < 4; i++)
            {
                network[i] = (byte)(ip[i] & mask[i]);
                broadcast[i] = (byte)(ip[i] | ~mask[i]);
            }

            var networkAddress = new IPAddress(network);
            yield return new NetworkSubnet(
                $"{networkAddress}/{prefix}", nic.Name, nic.Description, nic.NetworkInterfaceType,
                ua.Address, networkAddress, prefix, new IPAddress(broadcast));
        }
    }

    static int DefaultRank(NetworkSubnet subnet)
    {
        var virtualAdapter = IsVirtualOrVpn(subnet);
        var privateAddress = IsPrivate(subnet.LocalAddress);
        var ethernet = subnet.InterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Ethernet3Megabit;
        var wifi = subnet.InterfaceType == NetworkInterfaceType.Wireless80211;

        if (!virtualAdapter && privateAddress && ethernet) return 0;
        if (!virtualAdapter && privateAddress && wifi) return 1;
        if (!virtualAdapter && ethernet) return 2;
        if (!virtualAdapter && wifi) return 3;
        if (!virtualAdapter && privateAddress) return 4;
        return virtualAdapter ? 10 : 5;
    }

    static bool IsVirtualOrVpn(NetworkSubnet subnet)
    {
        var text = $"{subnet.InterfaceName} {subnet.InterfaceDescription}";
        string[] markers =
        [
            "virtual", "vpn", "hyper-v", "vmware", "virtualbox", "vbox", "wsl", "docker",
            "bluetooth", "tailscale", "zerotier", "wireguard", "tap", "tunnel"
        ];
        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 ||
               b[0] == 172 && b[1] is >= 16 and <= 31 ||
               b[0] == 192 && b[1] == 168;
    }

    static bool TryPrefixLength(IPAddress maskAddress, out int prefix)
    {
        prefix = 0;
        var zeroSeen = false;
        foreach (var value in maskAddress.GetAddressBytes())
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var set = (value & (1 << bit)) != 0;
                if (set && zeroSeen) return false;
                if (set) prefix++;
                else zeroSeen = true;
            }
        }
        return true;
    }
}
