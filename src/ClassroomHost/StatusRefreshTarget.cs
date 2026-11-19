// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>Live presentation object plus the endpoint identity admitted to a refresh queue; not a transport/credential snapshot.</summary>
public sealed record StatusRefreshTarget(ClientInfo Client, string Address, int Port, string ClientId, string Mac)
{
    public static StatusRefreshTarget Capture(ClientInfo client) => new(client, client.Address, client.Port, client.ClientId, client.Mac);

    public static bool IsIdleManaged(ClientInfo client) => client.Room.Length > 0 && client.BusyCount == 0 && client.ExclusiveOperation.Length == 0;

    public bool Matches() => Client.Address == Address && Client.Port == Port && Client.ClientId == ClientId && Client.Mac == Mac;
    public bool CanStart() => Matches() && IsIdleManaged(Client);
}
