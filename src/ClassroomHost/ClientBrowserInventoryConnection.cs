// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.BrowserInventory;
using ClassroomManager.Modules;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>The inventory application's transport adapter; no dialogs or view state.</summary>
public sealed class ClientBrowserInventoryConnection(ClientSession session, ClientInfo client) : IBrowserInventoryConnection
{
    readonly (BrowserOperations Operations, ModuleComputer Computer) _module = ModuleRuns.OpenBrowser(session, client);

    public async Task<JsonObject> ReadAsync(string level, string command, CancellationToken cancellation) =>
        JsonNode.Parse(await _module.Operations.ReadAsync(_module.Computer, level == Levels.System, command, cancellation))!.AsObject();
    public void Dispose() => session.Dispose();
}
