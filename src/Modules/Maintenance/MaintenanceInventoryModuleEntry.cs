// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Modules;

namespace ClassroomManager.Maintenance;

/// <summary>The module's entry for the Host: says who the module is and what it adds.</summary>
public sealed class MaintenanceInventoryModuleEntry : IClassroomModule
{
    public ModuleDescriptor Describe() =>
        new(MaintenanceInventoryModule.Id, "Windows maintenance inventory", ModuleContract.CurrentVersion, MaintenanceOperations.Commands)
        {
            Ports = [new(ModulePorts.ClientCall, 1)],
            SystemCommands = MaintenanceOperations.Commands,
            Icon =
            [
                @"   _  _",
                @" _| || |_",
                @"|  .--.  |",
                @" >-|  |-<",
                @"|_ '--' _|",
                @"  |_||_|",
            ],
            ScheduledActions =
            [
                new("updatescan", "Check for Windows updates (lists them, installs nothing)"),
                new("updateinstall", "Install Windows updates (never restarts the computer)"),
            ],
        };
}
