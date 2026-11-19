// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Modules;

namespace ClassroomManager.Automation;

/// <summary>The module's entry for the Host: says who the module is and what it adds.</summary>
public sealed class SchedulingModuleEntry : IClassroomModule
{
    public ModuleDescriptor Describe() =>
        new(SchedulingModule.Id, "Scheduled action orchestration", ModuleContract.CurrentVersion, [])
        {
            Icon =
            [
                @"   .-----.",
                @"  /  12   \",
                @" | 9  +  3 |",
                @"  \   6   /",
                @"   '-----'",
            ],
        };
}
