// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Modules;

namespace ClassroomManager.Reporting;

/// <summary>What the module asks of a computer for its report, through the run's ports only: one hardware and software snapshot.</summary>
public sealed class ReportingOperations(IModuleClient client)
{
    public const string Inventory = "inventory.get";

    public static IReadOnlyList<string> Commands { get; } = [Inventory];

    public Task<string> InventoryAsync(ModuleComputer computer, CancellationToken cancellation) =>
        client.CallAsSystemAsync(computer, Inventory, "{}", cancellation, TimeSpan.FromMinutes(5));
}
