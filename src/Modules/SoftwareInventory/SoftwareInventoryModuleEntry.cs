// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Modules;

namespace ClassroomManager.SoftwareInventory;

/// <summary>The module's entry for the Host: says who the module is and what it adds.</summary>
public sealed class SoftwareInventoryModuleEntry : IClassroomModule
{
    public ModuleDescriptor Describe() =>
        new(SoftwareInventoryModule.Id, "Software comparison", ModuleContract.CurrentVersion, SoftwareOperations.Commands)
        {
            Ports = [new(ModulePorts.ClientCall, 1)],
            SystemCommands = SoftwareOperations.Commands,
            Icon =
            [
                @"  .-------.",
                @" /       /|",
                @".-------. |",
                @"| apps  | '",
                @"|  []   |/",
                @"'-------'",
            ],
        };
}
