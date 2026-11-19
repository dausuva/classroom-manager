// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Reconciles discovered identities into the live roster on its owning thread. Never approves trust.</summary>
public sealed class HostRoster(IList<ClientInfo> clients)
{
    public ClientInfo Add(string address, string name, int port = 0)
    {
        var existing = clients.FirstOrDefault(c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var client = new ClientInfo { Address = address, Name = name, Port = port, Status = "Discovered - assign to a room to monitor" };
        clients.Add(client);
        return client;
    }

    public ClientInfo Merge(Discovery.Found found)
    {
        var address = found.Address.ToString();
        var clientId = found.Info.ClientId.Trim();
        var mac = ComputerIdentity.NormalizeMac(found.Info.Mac);
        var computer = clients.Where(c => ComputerIdentity.StableMatch(c.ClientId, c.Mac, clientId, mac))
            .OrderByDescending(c => c.Room.Length > 0).ThenByDescending(c => c.ClientId.Length > 0).FirstOrDefault();
        computer ??= clients.FirstOrDefault(c => ComputerIdentity.SafeAddressMatch(c, address, clientId, mac));
        if (computer is null)
        {
            computer = new ClientInfo { Status = "Discovered - assign to a room to monitor" };
            clients.Add(computer);
        }
        // Names never establish identity: cloned classroom images often share their hostnames.
        var duplicates = clients.Where(c => !ReferenceEquals(c, computer) &&
            (ComputerIdentity.StableMatch(c.ClientId, c.Mac, clientId, mac) ||
             (c.Room.Length == 0 && c.Address.Equals(address, StringComparison.OrdinalIgnoreCase) &&
              !ComputerIdentity.HasStableIdentity(c.ClientId, c.Mac)))).ToList();
        foreach (var duplicate in duplicates) clients.Remove(duplicate);
        computer.Address = address;
        computer.Name = found.Info.MachineName;
        computer.Port = found.Info.TcpPort;
        if (clientId.Length > 0) computer.ClientId = clientId;
        if (mac.Length > 0) computer.Mac = mac;
        return computer;
    }
}
