// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// Commands that need an Admin key on the computer but do not ask for a step-up: they read, or change something that is easy to undo. A test fails
/// when a command that needs an Admin key is in neither this list nor <see cref="StepUp.Dangerous"/>, so a new command is classified on purpose.
/// </summary>
public static class StepUpRoutine
{
    public static readonly IReadOnlySet<string> Commands = new HashSet<string>(StringComparer.Ordinal)
    {
        Cmd.AccessKeysList,
        Cmd.AccountEnable,
        Cmd.AccountIdentityList,
        Cmd.AccountIdentityOpenSettings,
        Cmd.AccountList,
        Cmd.AppAccountsList,
        Cmd.AutorunList,
        Cmd.BrowserDefaultSet,
        Cmd.BrowserList,
        Cmd.BrowserPolicyGet,
        Cmd.BrowserPolicySet,
        Cmd.BrowserProfilesList,
        Cmd.DesktopLayoutReset,
        Cmd.FsList,
        Cmd.FsMkdir,
        Cmd.FsRename,
        Cmd.FsRoots,
        Cmd.InventoryGet,
        Cmd.KeyboardList,
        Cmd.KeyboardSet,
        Cmd.LanguageInstall,
        Cmd.LockScreenReset,
        Cmd.LockScreenSet,
        Cmd.MouseReset,
        Cmd.MouseSet,
        Cmd.PowerDisplayGet,
        Cmd.PowerDisplaySet,
        Cmd.ProcCloseApps,
        Cmd.ProcList,
        Cmd.ProgramsList,
        Cmd.RecycleBinNameReset,
        Cmd.RegistryExport,
        Cmd.RegistryList,
        Cmd.ScreenSaverGet,
        Cmd.ScreenSaverReset,
        Cmd.ScreenSaverSet,
        Cmd.SignInImageDefault,
        Cmd.SignInImageSet,
        Cmd.TasksList,
        Cmd.TasksSetEnabled,
        Cmd.TermClose,
        Cmd.TermRead,
        Cmd.AppearanceReset,
        Cmd.AppearanceSet,
        Cmd.TermWrite,
        Cmd.TimeSync,
        Cmd.UserSignIn,
        Cmd.WallpaperManagedDelete,
        Cmd.WallpaperManagedList,
        Cmd.WallpaperManagedPreview,
        Cmd.WallpaperManagedSet,
        Cmd.WindowsPreferencesSet,
        Cmd.WindowsThemeSet,
        Cmd.WindowsThemesList,
        Cmd.WindowsUpdatePause,
        Cmd.WindowsUpdateResume,
        Cmd.WindowsUpdateScan,
        Cmd.WindowsUpdateStatus,
        Cmd.WindowsUpdateTargetSet,
    };
}
