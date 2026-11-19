// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class HostRosterTests
{
    static Discovery.Found Found(string address, string id, string mac, string name = "Cloned hostname") =>
        new(IPAddress.Parse(address), new DiscoveryReply { MachineName = name, ClientId = id, Mac = mac, TcpPort = 6000 });

    [Fact]
    public void Stable_identity_keeps_the_managed_row_and_removes_only_its_duplicates()
    {
        var managed = new ClientInfo { Address = "192.0.2.1", ClientId = "id", Mac = "001122334455", Room = "Lab", Alias = "Teacher label", IsSelected = true };
        var duplicate = new ClientInfo { Address = "192.0.2.2", ClientId = "id" };
        var legacy = new ClientInfo { Address = "192.0.2.10" };
        var other = new ClientInfo { Address = "192.0.2.3", ClientId = "other", Mac = "112233445566" };
        var clients = new List<ClientInfo> { duplicate, managed, legacy, other };
        Assert.Same(managed, new HostRoster(clients).Merge(Found("192.0.2.10", "id", "00-11-22-33-44-55")));
        Assert.Equal([managed, other], clients);
        Assert.Equal("Lab", managed.Room);
        Assert.Equal("Teacher label", managed.Name);
        Assert.Equal("Cloned hostname", managed.ComputerName);
        Assert.True(managed.IsSelected);
        Assert.Equal("192.0.2.10", managed.Address);
        Assert.Equal("001122334455", managed.Mac);
        Assert.Equal(6000, managed.Port);
    }

    [Fact]
    public void Cloned_names_and_client_ids_do_not_merge_different_hardware_or_reassign_rooms()
    {
        var old = new ClientInfo { Address = "192.0.2.1", Name = "Cloned hostname", ClientId = "clone", Mac = "001122334455", Room = "Lab" };
        var clients = new List<ClientInfo> { old };
        var found = new HostRoster(clients).Merge(Found("192.0.2.1", "clone", "112233445566"));
        Assert.NotSame(old, found);
        Assert.Equal(2, clients.Count);
        Assert.Equal("", found.Room);
        Assert.Equal("001122334455", old.Mac);
        Assert.Equal("Lab", old.Room);
    }

    [Fact]
    public void Manual_add_is_idempotent_and_keeps_existing_identity_and_label()
    {
        var clients = new List<ClientInfo>();
        var roster = new HostRoster(clients);
        var first = roster.Add("HOSTNAME", "Saved", 6000);
        Assert.Same(first, roster.Add("hostname", "Replacement", 7000));
        Assert.Single(clients);
        Assert.Equal("Saved", first.Name);
        Assert.Equal(6000, first.Port);
    }
}
