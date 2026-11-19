// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Modules;

namespace ClassroomManager.StudentWork;

/// <summary>The module's entry for the Host: says who the module is and what it adds.</summary>
public sealed class StudentBackupModuleEntry : IClassroomModule
{
    public ModuleDescriptor Describe() =>
        new(StudentBackupModule.Id, "Student-work backups", ModuleContract.CurrentVersion, ["fs.recent"])
        {
            Ports = [new(ModulePorts.ClientCall, 1), new(ModulePorts.ClientTransfer, 1), new(ModulePorts.StorageSink, 1)],
            Sinks = [PortBackupSource.Sink],
            Icon =
            [
                @" ___",
                @"/   \_____",
                @"| files  |",
                @"|   |    |",
                @"|  \|/   |",
                @"|___v____|",
            ],
            ScheduledActions = [new("backup", "Back up student work (what changed since the last backup)")],
        };
}
