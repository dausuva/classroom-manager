// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Modules;

namespace ClassroomManager.BrowserInventory;

/// <summary>The module's entry for the Host: says who the module is and what it adds.</summary>
public sealed class BrowserInventoryModuleEntry : IClassroomModule
{
    public ModuleDescriptor Describe() =>
        new(BrowserInventoryModule.Id, "Browser inventory reports", ModuleContract.CurrentVersion, BrowserOperations.Commands)
        {
            Ports = [new(ModulePorts.ClientCall, 1)],
            SystemCommands = BrowserOperations.SystemCommands,
            Icon =
            [
                @" .----------.",
                @"/  .-----.   \",
                @"|  | www |   |",
                @"\  '-----'   /",
                @" '----------'",
            ],
        };
}
