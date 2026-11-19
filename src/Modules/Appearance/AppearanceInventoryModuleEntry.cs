// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Modules;

namespace ClassroomManager.Appearance;

/// <summary>The module's entry for the Host: says who the module is and what it adds.</summary>
public sealed class AppearanceInventoryModuleEntry : IClassroomModule
{
    public ModuleDescriptor Describe() =>
        new(AppearanceInventoryModule.Id, "Appearance", ModuleContract.CurrentVersion, ["screensaver.get", "screensaver.set", .. AppearanceOperations.Commands, .. WallpaperOperations.Commands])
        {
            Ports = [new(ModulePorts.ClientCall, 1), new(ModulePorts.ClientUpload, 1), new(ModulePorts.StorageSource, 1)],
            Uploads = [ModuleUploads.Wallpaper],
            ScheduledActions = [new("appearance", "Apply an Appearance preset (give its name in the text)")],
            SystemCommands = [.. WallpaperOperations.SystemCommands],
            Icon =
            [
                @".----------.",
                @"| *  .  *  |",
                @"|  .  *  . |",
                @"| *  .   * |",
                @"'-----.----'",
                @"   _/___\_",
            ],
        };
}
