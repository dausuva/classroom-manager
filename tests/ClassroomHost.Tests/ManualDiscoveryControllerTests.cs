// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ManualDiscoveryControllerTests
{
    sealed class View : IManualDiscoveryView
    {
        public bool CanScan { get; set; } = true;
        public Discovery.NetworkSubnet? CurrentNetwork { get; set; }
        public List<string> Events = [];
        public List<string> Logs = [];
        public void SetScanning(bool scanning) { CanScan = !scanning; Events.Add(scanning ? "busy" : "idle"); }
        public Discovery.SubnetSelection Prepare() { Events.Add("prepare"); return new(CurrentNetwork, false); }
        public Task RefreshAsync() { Events.Add("refresh"); return Task.CompletedTask; }
        public void RefreshRooms() => Events.Add("rooms");
        public void Log(string message) => Logs.Add(message);
    }

    static readonly string Pin = new('A', 64);
    static EnrolmentOffer Offer(int last = 10) => new(IPAddress.Parse("192.0.2." + last), 6000, Protocol.Minor, Pin);
    static Discovery.Found Found() => new(IPAddress.Parse("192.0.2.10"), new DiscoveryReply
        { MachineName = "Classroom PC", ClientId = "id", Mac = "001122334455", TcpPort = 6000 });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Uses_core_enrolment_and_persists_before_refresh_without_any_real_network(bool autoEnrol)
    {
        using var coordinator = new DiscoveryWorkCoordinator(TimeSpan.FromMinutes(1));
        var settings = new HostSettings { AutoEnrolNewComputers = autoEnrol };
        var clients = new List<ClientInfo>();
        var view = new View();
        var probes = 0;
        var controller = new ManualDiscoveryController(view, settings, new(clients), coordinator,
            () => { Assert.Single(clients); view.Events.Add("save"); },
            (_, _) => Task.FromResult(new ManualDiscoveryBatch([Found()], [Offer()])),
            (_, _) => { probes++; return Task.FromResult(Pin); });
        Assert.True(await controller.ScanAsync());
        Assert.Equal(autoEnrol ? 1 : 0, probes);
        Assert.Equal(autoEnrol ? 1 : 0, settings.TrustedTlsEndpoints.Count);
        Assert.Equal("id", Assert.Single(clients).ClientId);
        Assert.Equal(["busy", "prepare", "save", "refresh", "rooms", "idle"], view.Events);
        if (!autoEnrol) Assert.Contains(view.Logs, message => message.Contains("automatic enrolment is off"));
    }

    [Theory]
    [InlineData("udp")]
    [InlineData("tcp")]
    [InlineData("subnet")]
    [InlineData("keys")]
    [InlineData("auto")]
    [InlineData("network")]
    public async Task Changed_configuration_discards_late_results_without_roster_or_trust_changes(string change)
    {
        using var coordinator = new DiscoveryWorkCoordinator(TimeSpan.FromMinutes(1));
        var settings = new HostSettings();
        var clients = new List<ClientInfo>();
        var view = new View();
        var controller = new ManualDiscoveryController(view, settings, new(clients), coordinator,
            () => throw new Exception("Stale results cannot save"), (_, _) =>
            {
                switch (change)
                {
                    case "udp": settings.UdpPort++; break;
                    case "tcp": settings.TcpPort++; break;
                    case "subnet": settings.DiscoverySubnet = "other"; break;
                    case "keys": settings.Psk = "synthetic-different-key"; break;
                    case "auto": settings.AutoEnrolNewComputers = false; break;
                    case "network": view.CurrentNetwork = new("other", "Ethernet", "Synthetic", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
                        IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.0"), 24, IPAddress.Parse("192.0.2.255")); break;
                }
                return Task.FromResult(new ManualDiscoveryBatch([Found()], [Offer()]));
            }, (_, _) => throw new Exception("Stale results cannot probe"));
        Assert.True(await controller.ScanAsync());
        Assert.Empty(clients);
        Assert.Empty(settings.TrustedTlsEndpoints);
        Assert.DoesNotContain("refresh", view.Events);
        Assert.True(view.CanScan);
    }

    [Fact]
    public async Task Key_change_while_certificate_is_awaited_cannot_approve_the_old_offer()
    {
        using var coordinator = new DiscoveryWorkCoordinator(TimeSpan.FromMinutes(1));
        var settings = new HostSettings();
        var clients = new List<ClientInfo>();
        var view = new View();
        var saves = 0;
        var controller = new ManualDiscoveryController(view, settings, new(clients), coordinator, () => saves++,
            (_, _) => Task.FromResult(new ManualDiscoveryBatch([], [Offer()])),
            (_, _) => { settings.Psk = "synthetic-changed-key"; return Task.FromResult(Pin); });
        Assert.True(await controller.ScanAsync());
        Assert.Equal(1, saves);
        Assert.Empty(clients);
        Assert.Empty(settings.TrustedTlsEndpoints);
        Assert.DoesNotContain("refresh", view.Events);
    }

    [Fact]
    public async Task An_approval_added_while_certificate_is_awaited_is_never_replaced()
    {
        using var coordinator = new DiscoveryWorkCoordinator(TimeSpan.FromMinutes(1));
        var settings = new HostSettings();
        var clients = new List<ClientInfo>();
        var view = new View();
        var other = new string('B', 64);
        var controller = new ManualDiscoveryController(view, settings, new(clients), coordinator, () => { },
            (_, _) => Task.FromResult(new ManualDiscoveryBatch([], [Offer()])),
            (offer, _) => { settings.EnrollTrust(offer.Address.ToString(), offer.Port, "", [other]); return Task.FromResult(Pin); });
        Assert.True(await controller.ScanAsync());
        Assert.Equal([other], Assert.Single(settings.TrustedTlsEndpoints).CertificateSha256);
        Assert.Empty(clients);
    }

    [Fact]
    public async Task Partial_enrolment_failure_still_saves_the_already_accepted_row_and_pin()
    {
        using var coordinator = new DiscoveryWorkCoordinator(TimeSpan.FromMinutes(1));
        var settings = new HostSettings();
        var clients = new List<ClientInfo>();
        var view = new View();
        var saved = false;
        var controller = new ManualDiscoveryController(view, settings, new(clients), coordinator,
            () => { Assert.Single(clients); Assert.Single(settings.TrustedTlsEndpoints); saved = true; },
            (_, _) => Task.FromResult(new ManualDiscoveryBatch([], [Offer(), Offer(11)])),
            (offer, _) => offer.Address.Equals(Offer().Address) ? Task.FromResult(Pin) : throw new InvalidOperationException("synthetic TLS failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ScanAsync());
        Assert.True(saved);
        Assert.Equal("192.0.2.10", Assert.Single(clients).Address);
        Assert.DoesNotContain("refresh", view.Events);
        Assert.True(view.CanScan);
    }
}
