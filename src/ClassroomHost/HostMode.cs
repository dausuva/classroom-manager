// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public static class HostModes
{
    public const string Teacher = "Teacher";
    public const string Admin = "Admin";
}

public sealed record TeacherCommandDefinition(string Id, string Group, string Label, bool VisibleByDefault = false);

public static class TeacherCommandCatalog
{
    public static readonly IReadOnlyList<TeacherCommandDefinition> All =
    [
        new("computers.scan", "Computers", "Scan network", true),
        new("computers.add", "Computers", "Add computer..."),
        new("computers.remove", "Computers", "Remove"),
        new("computers.remove-offline", "Computers", "Remove offline"),
        new("computers.export", "Computers", "Export list..."),
        new("computers.import", "Computers", "Import list..."),
        new("computers.wake", "Computers", "Wake up", true),
        new("computers.sync-time", "Classroom", "Sync time..."),
        new("computers.sign-in", "Computers", "Sign in user..."),
        new("computers.refresh", "Computers", "Refresh status", true),
        new("computers.assign-room", "Computers", "Assign to room...", true),
        new("computers.columns", "Computers", "Columns...", true),
        new("computers.feedback", "Help", "Send feedback...", true),

        new("remote.install", "Software", "Install / uninstall..."),
        new("remote.staged-uploads", "Software", "Temporary uploads..."),
        new("remote.programs", "Software", "Programs..."),
        new("remote.update-client", "Software", "Update client..."),
        new("remote.uninstall-client", "Software", "Uninstall client..."),
        new("remote.command", "Software", "Run command..."),
        new("remote.open-website", "Classroom", "Open website...", true),
        new("remote.open-program", "Classroom", "Open program...", true),
        new("remote.terminal", "Software", "Terminal..."),
        new("remote.processes", "Software", "Processes..."),
        new("remote.windows-update", "Computers", "Windows Update..."),
        new("remote.registry", "Software", "Registry..."),
        new("remote.tasks", "Software", "Scheduled tasks..."),
        new("remote.autoruns", "Software", "Autoruns..."),
        new("remote.power", "Computers", "Power...", true),
        new("settings.custom-quick-actions", "Settings", "Custom quick actions...", true),
        new("settings.arrange-toolbar", "Settings", "Arrange toolbar...", true),
        new("remote.cancel-power", "Computers", "Cancel pending shutdown", true),

        new("restrictions.websites", "Restrictions", "Block websites..."),
        new("restrictions.sites-in-use", "Restrictions", "Websites in use..."),
        new("restrictions.browsers", "Software", "Browsers..."),
        new("restrictions.website-sync", "Restrictions", "Website blocklist sync..."),
        new("restrictions.apps", "Restrictions", "Block apps..."),
        new("restrictions.exam", "Restrictions", "Exam mode", true),
        new("restrictions.exam-settings", "Restrictions", "Exam mode settings...", true),
        new("restrictions.accounts", "Reports", "Local accounts..."),
        new("restrictions.signed-in-accounts", "Reports", "Signed-in accounts...", true),
        new("restrictions.app-accounts", "Reports", "App accounts..."),
        new("restrictions.inventory", "Reports", "Export inventory..."),

        new("classroom.send-files", "Files", "Send files...", true),
        new("classroom.transfer-history", "Files", "Transfer history...", true),
        new("classroom.collect-files", "Files", "Collect files...", true),
        new("classroom.recent-files", "Files", "Recent files...", true),
        new("classroom.browse-files", "Files", "Browse files..."),
        new("classroom.message", "Classroom", "Message...", true),
        new("classroom.hide-messages", "Classroom", "Hide messages", true),
        new("classroom.lock", "Classroom", "Lock screen...", true),
        new("classroom.unlock", "Classroom", "Unlock", true),
        new("classroom.wallpaper", "Classroom", "Set wallpaper...", true),
        new("classroom.restore-wallpaper", "Classroom", "Restore previous wallpaper...", true),
        new("classroom.managed-wallpapers", "Classroom", "Managed wallpapers..."),
        new("classroom.recent-wallpapers", "Classroom", "Recent wallpapers..."),
        new("classroom.themes", "Classroom", "Windows themes..."),
        new("classroom.appearance-settings", "Classroom", "Appearance settings..."),
        new("classroom.appearance", "Classroom", "Windows appearance & language..."),
        new("classroom.screen-saver", "Classroom", "Screen saver..."),
        new("classroom.mouse", "Classroom", "Mouse..."),
        new("classroom.keyboard", "Classroom", "Keyboard layouts..."),
        new("classroom.reset-mouse", "Classroom", "Reset mouse"),
        new("classroom.recycle-bin", "Classroom", "Recycle Bin..."),
        new("classroom.reset-desktop-layout", "Classroom", "Reset desktop layout..."),

        new("settings.host", "Settings", "Host settings...", true),
        new("settings.interface", "Settings", "Interface settings...", true),
        new("settings.languages", "Settings", "Languages...", true),
        new("settings.help", "Help", "Help", true),
        new("settings.message-to-admin", "Settings", "Message to administrator...", true),
        new("settings.messages-from-teachers", "Settings", "Messages from teachers..."),
        new("settings.setup-assistant", "Settings", "Setup assistant...", true),
        new("settings.command-favourites", "Settings", "Command favourites..."),
        new("settings.crash-logs", "Settings", "Crash logs..."),
        new("settings.lab-keys", "Settings", "Lab keys..."),
        new("settings.access-keys", "Settings", "Access keys..."),
        new("settings.rotate-key", "Settings", "Rotate shared key..."),
        new("settings.export-teacher", "Settings", "Export teacher settings..."),
        new("settings.export-configuration", "Settings", "Export configuration..."),
        new("settings.import-configuration", "Settings", "Import configuration...", true),
        new("settings.backups", "Settings", "Settings backups...", true),
        new("settings.import-teacher", "Settings", "Import teacher settings...", true),
        new("settings.schedules", "Settings", "Schedules..."),
        new("settings.about", "Help", "About...", true),
        new("settings.licenses", "Help", "Open-source licenses...", true),
        new("settings.feedback", "Help", "Review saved feedback...", true),

    ];

    /// <summary>
    /// The client command each action is built on. An action whose command needs an Admin key (see
    /// CommandAccess, which the computers enforce) cannot be offered to Teacher mode: a Teacher key would
    /// be refused by every computer. An action not listed here works on this Host alone.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Sends = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["computers.refresh"] = Cmd.Ping,
        ["computers.sync-time"] = Cmd.TimeSync,
        ["computers.sign-in"] = Cmd.UserSignIn,
        ["remote.install"] = Cmd.InstallRun,
        ["remote.staged-uploads"] = Cmd.StagingList,
        ["remote.programs"] = Cmd.ProgramsList,
        ["remote.update-client"] = Cmd.SelfUpdate,
        ["remote.uninstall-client"] = Cmd.ClientUninstall,
        ["remote.command"] = Cmd.CmdRun,
        ["remote.open-website"] = Cmd.WebsiteOpen,
        ["remote.open-program"] = Cmd.ProgramOpen,
        ["remote.terminal"] = Cmd.TermStart,
        ["remote.processes"] = Cmd.ProcList,
        ["remote.windows-update"] = Cmd.WindowsUpdateScan,
        ["remote.registry"] = Cmd.RegistryList,
        ["remote.tasks"] = Cmd.TasksList,
        ["remote.autoruns"] = Cmd.AutorunList,
        ["remote.power"] = Cmd.PowerAction,
        ["remote.cancel-power"] = Cmd.PowerAction,
        ["restrictions.websites"] = Cmd.BlockHostsSet,
        ["restrictions.sites-in-use"] = Cmd.BrowserSites,
        ["restrictions.browsers"] = Cmd.BrowserPolicySet,
        ["restrictions.website-sync"] = Cmd.BlockHostsSet,
        ["restrictions.apps"] = Cmd.BlockAppsSet,
        ["restrictions.exam"] = Cmd.ExamModeSet,
        ["restrictions.accounts"] = Cmd.AccountList,
        ["restrictions.signed-in-accounts"] = Cmd.AccountIdentityList,
        ["restrictions.app-accounts"] = Cmd.AppAccountsList,
        ["restrictions.inventory"] = Cmd.InventoryGet,
        ["classroom.send-files"] = Cmd.FsDeliver,
        ["classroom.transfer-history"] = Cmd.FsDeliveredDelete,
        ["classroom.collect-files"] = Cmd.FsFind,
        ["classroom.recent-files"] = Cmd.FsRecent,
        ["classroom.browse-files"] = Cmd.FsList,
        ["classroom.message"] = Cmd.MessageShow,
        ["classroom.hide-messages"] = Cmd.MessageClear,
        ["classroom.lock"] = Cmd.LockSet,
        ["classroom.unlock"] = Cmd.LockClear,
        // The desktop wallpaper; only its sign-in screen choice needs an Admin key, and the computers say so.
        ["classroom.wallpaper"] = Cmd.WallpaperSet,
        ["classroom.restore-wallpaper"] = Cmd.WallpaperReset,
        ["classroom.managed-wallpapers"] = Cmd.WallpaperManagedList,
        ["classroom.recent-wallpapers"] = Cmd.WallpaperRecentList,
        ["classroom.themes"] = Cmd.WindowsThemesList,
        ["classroom.appearance-settings"] = Cmd.AppearanceCatalog,
        ["classroom.appearance"] = Cmd.WindowsPreferencesSet,
        ["classroom.screen-saver"] = Cmd.ScreenSaverSet,
        ["classroom.mouse"] = Cmd.MouseSet,
        ["classroom.keyboard"] = Cmd.KeyboardSet,
        ["classroom.reset-mouse"] = Cmd.MouseReset,
        ["classroom.recycle-bin"] = Cmd.RecycleBinEmpty,
        ["classroom.reset-desktop-layout"] = Cmd.DesktopLayoutReset,
        ["settings.access-keys"] = Cmd.AccessKeysSet,
        ["settings.rotate-key"] = Cmd.KeySet,
    };

    /// <summary>True when the action is refused to a Teacher key, so Teacher mode never shows it.</summary>
    public static bool NeedsAdminKey(string id) =>
        Sends.TryGetValue(id, out var command) && CommandAccess.RequiredRole(command) == OperatorRole.Admin;

    /// <summary>Shown in Teacher mode whatever is chosen: the way back to arranging the toolbar and the interface.</summary>
    public static readonly IReadOnlySet<string> AlwaysVisible = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "settings.arrange-toolbar", "settings.interface",
    };

    /// <summary>
    /// Commands made visible by default after Hosts were already in use, with the settings version that added
    /// each. Saved settings get each one once (see HostSettings.TeacherDefaultsVersion); a teacher who then
    /// hides it keeps it hidden.
    /// </summary>
    public static readonly IReadOnlyList<(int Version, string Id)> AddedDefaults =
    [
        (1, "remote.power"),
        (2, "classroom.appearance-settings"),
    ];

    public static List<string> Defaults() => All.Where(command => command.VisibleByDefault).Select(command => command.Id).ToList();

    public static List<string> Normalize(IEnumerable<string>? ids)
    {
        var allowed = All.Select(command => command.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (ids ?? Defaults()).Where(allowed.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
