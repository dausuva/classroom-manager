// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassroomManager.Shared;

public static class Protocol
{
    public const int Major = 1;
    public const int Minor = 21;
    public const int DefaultTcpPort = 5050;
    public const int DefaultUdpPort = 5051;
    public const string DiscoverMagic = "CLASSROOM_DISCOVER_V1";
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    public const int ChunkSize = 512 * 1024;
    public const string Version = "1.21";

    /// <summary>The protocol from which a Client answers the authenticated discovery probe (T-185).</summary>
    public const string AuthenticatedDiscoveryVersion = "1.18";

    /// <summary>
    /// The protocol from which a Client understands the bound handshake (the key proof covers the TLS certificate, T-187). A Host binds
    /// only to a Client that reports at least this version; to an older one it sends the plain proof.
    /// </summary>
    public const string ChannelBindingVersion = "1.17";

    /// <summary>Optional commands this build explicitly advertises from Ping.</summary>
    public static readonly string[] Capabilities =
    [
        Cmd.WindowsPreferencesSet,
        Cmd.WindowsPreferencesGet,
        Cmd.LanguageInstall,
        Cmd.WallpaperDefault,
        Cmd.WallpaperColor,
        Cmd.WallpaperManagedList,
        Cmd.WallpaperManagedSet,
        Cmd.WallpaperManagedDelete,
        Cmd.WallpaperManagedPreview,
        Cmd.WindowsThemesList,
        Cmd.WindowsThemeSet,
        Cmd.WindowsUpdateScan,
        Cmd.WindowsUpdateInstall,
        Cmd.WindowsUpdatePause,
        Cmd.WindowsUpdateResume,
        Cmd.WindowsUpdateStatus,
        Cmd.StagingList,
        Cmd.StagingDelete,
        Cmd.FsDeliveredDelete,
        Cmd.WindowsUpdateRestart,
        Cmd.AccountIdentityList,
        Cmd.AccountIdentityRemove,
        Cmd.AccountIdentityOpenSettings,
        Cmd.BlockAppsList,
        Cmd.ExamModeSet,
        Cmd.ExamModeClear,
        Cmd.MessageClear,
        Cmd.WebsiteOpen,
        Cmd.ProgramOpen,
        Cmd.DesktopLayoutReset,
        Cmd.ProcWindows,
        Cmd.ProcCloseApps,
        Cmd.BlockHostsReset,
        Cmd.ScreenSaverGet,
        Cmd.ScreenSaverSet,
        Cmd.ScreenSaverReset,
        Cmd.SignInImageSet,
        Cmd.SignInImageDefault,
        Cmd.ExamDevicesSet,
        Cmd.UserSignIn,
        Cmd.AccessKeysList,
        Cmd.AccessKeysSet,
        Cmd.AccessKeysRemove,
        Cmd.PowerDisplayGet,
        Cmd.PowerDisplaySet,
        Cmd.DisplayWake,
        Cmd.RecycleBinEmpty,
        Cmd.RecycleBinNameReset,
        Cmd.BrowserSites,
        Cmd.BrowserPolicyGet,
        Cmd.BrowserPolicySet,
        Cmd.BrowserDataClear,
        Cmd.BrowserProfilesList,
        Cmd.BrowserProfilesRemove,
        Cmd.BlockHostsAdd,
        Cmd.WindowsUpdateTargetSet,
        Cmd.KeyboardList,
        Cmd.KeyboardSet,
        Cmd.WallpaperRecentList,
        Cmd.WallpaperRecentRemove,
        Cmd.LockScreenReset,
        Cmd.BrowserList,
        Cmd.BrowserDefaultSet,
        Cmd.AppAccountsList,
        Cmd.LockScreenSet,
        Cmd.ClientUninstall,
        Cmd.SystemRename,
        Cmd.FsRecent,
        Cmd.AppearanceCatalog,
        Cmd.AppearanceGet,
        Cmd.AppearanceSet,
        Cmd.AppearanceReset,
    ];

    public static string RequiredVersion(string command) => command switch
    {
        Cmd.WindowsPreferencesSet or Cmd.LanguageInstall => "1.2",
        Cmd.WallpaperDefault or Cmd.WallpaperColor or Cmd.WallpaperManagedList or
        Cmd.WallpaperManagedDelete or Cmd.WindowsThemesList or Cmd.WindowsThemeSet => "1.3",
        Cmd.WindowsUpdateScan or Cmd.WindowsUpdateInstall or Cmd.WindowsUpdatePause or
        Cmd.WindowsUpdateResume or Cmd.WindowsUpdateRestart => "1.4",
        Cmd.AccountIdentityList or Cmd.AccountIdentityRemove or Cmd.AccountIdentityOpenSettings => "1.4",
        Cmd.WallpaperManagedSet or Cmd.WallpaperManagedPreview or Cmd.BlockAppsList or Cmd.ExamModeSet or Cmd.ExamModeClear or
        Cmd.MessageClear or Cmd.WebsiteOpen or Cmd.ProgramOpen or Cmd.DesktopLayoutReset or Cmd.ProcWindows or Cmd.ProcCloseApps or Cmd.BlockHostsReset => "1.5",
        Cmd.WindowsUpdateStatus or Cmd.WindowsPreferencesGet => "1.6",
        Cmd.StagingList or Cmd.StagingDelete or Cmd.FsDeliveredDelete => "1.7",
        Cmd.ScreenSaverGet or Cmd.ScreenSaverSet or Cmd.ScreenSaverReset or
        Cmd.SignInImageSet or Cmd.SignInImageDefault or Cmd.ExamDevicesSet or Cmd.UserSignIn or
        Cmd.AccessKeysList or Cmd.AccessKeysSet or Cmd.AccessKeysRemove => "1.8",
        Cmd.PowerDisplayGet or Cmd.PowerDisplaySet => "1.9",
        Cmd.DisplayWake or Cmd.RecycleBinEmpty or Cmd.RecycleBinNameReset or Cmd.BrowserSites or
        Cmd.BrowserPolicyGet or Cmd.BrowserPolicySet or Cmd.BrowserDataClear or Cmd.BrowserProfilesList or Cmd.BrowserProfilesRemove or Cmd.BlockHostsAdd or Cmd.WindowsUpdateTargetSet => "1.10",
        Cmd.KeyboardList or Cmd.KeyboardSet => "1.11",
        Cmd.WallpaperRecentList or Cmd.WallpaperRecentRemove or Cmd.LockScreenReset or Cmd.BrowserList or Cmd.BrowserDefaultSet or Cmd.AppAccountsList => "1.12",
        Cmd.LockScreenSet => "1.13",
        Cmd.ClientUninstall => "1.14",
        Cmd.SystemRename => ComputerNameRules.RequiredVersion,
        Cmd.FsRecent => RecentFiles.RequiredVersion,
        Cmd.AppearanceCatalog or Cmd.AppearanceGet or Cmd.AppearanceSet or Cmd.AppearanceReset => AppearanceWire.RequiredVersion,
        _ => "1.0",
    };

    /// <summary>
    /// Capability advertisements take precedence. Legacy clients do not send them, so their
    /// semantic protocol version is used as a conservative compatibility fallback.
    /// </summary>
    public static bool SupportsCommand(string command, string clientVersion,
        IEnumerable<string> capabilities, bool capabilitiesAdvertised)
    {
        if (!TryParseVersion(clientVersion, out var clientMajor, out var clientMinor) ||
            !TryParseVersion(RequiredVersion(command), out var requiredMajor, out var requiredMinor)) return false;
        if (clientMajor != requiredMajor) return false;

        // Baseline commands predate capability advertisements and remain available throughout the
        // same major protocol. Only later optional commands require an explicit advertised feature.
        if (requiredMinor == 0) return true;
        if (capabilitiesAdvertised)
            return capabilities.Contains(command, StringComparer.OrdinalIgnoreCase);
        return clientMajor == requiredMajor && clientMinor >= requiredMinor;
    }

    /// <summary>True when <paramref name="version"/> (a protocol version such as "1.14") is the required one or a later one of the same major version.</summary>
    public static bool IsAtLeast(string? version, string required) =>
        TryParseVersion(version ?? "", out var major, out var minor) && TryParseVersion(required, out var requiredMajor, out var requiredMinor) &&
        major == requiredMajor && minor >= requiredMinor;

    public static bool TryParseVersion(string value, out int major, out int minor)
    {
        major = minor = 0;
        if (!System.Version.TryParse(value, out var parsed)) return false;
        major = parsed.Major;
        minor = Math.Max(0, parsed.Minor);
        return true;
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}

/// <summary>Command names understood by the client.</summary>
public static class Cmd
{
    public const string Ping = "ping";

    public const string InstallRun = "install.run";        // run an already-uploaded installer
    public const string InstallWinget = "install.winget";  // winget install/uninstall --id X (args.action)

    public const string SelfUpdate = "self.update";        // replace ClassroomClient.exe with an already-uploaded build and restart the service
    public const string ClientUninstall = "client.uninstall";  // removes the client from the PC (1.14); keepData keeps its settings for a reinstall. The answer comes before the removal starts

    public const string SystemRename = "system.rename";    // gives the PC a new Windows computer name (1.16), applied after a restart; refused on a domain

    public const string KeySet = "key.set";                // replace the shared key (args.key); new connections use it, the current one keeps working

    public const string AccessKeysList = "key.access.list";     // names, roles and fingerprints of the keys this client accepts (never the keys)
    public const string AccessKeysSet = "key.access.set";       // add or replace a named key with a role (Teacher or Admin)
    public const string AccessKeysRemove = "key.access.remove"; // revoke a named key; the lab's original key cannot be removed

    public const string ProgramsList = "programs.list";
    public const string ProgramsUninstall = "programs.uninstall";

    public const string CmdRun = "cmd.run";

    public const string TimeSync = "time.sync";            // set the clock (and optionally time zone)

    public const string PowerAction = "power.action";      // shutdown | restart | logoff | cancel
    public const string PowerDisplayGet = "power.display.get"; // when the display turns off, plugged in and on battery
    public const string PowerDisplaySet = "power.display.set"; // set the plugged-in display-off time of the active power plan, or restore the earlier one

    public const string LockSet = "lock.set";
    public const string LockClear = "lock.clear";

    public const string MessageShow = "message.show";
    public const string MessageClear = "message.clear";
    public const string WebsiteOpen = "launch.website";
    public const string ProgramOpen = "launch.program";
    public const string ProcWindows = "proc.windows";
    public const string BrowserSites = "browser.sites";         // domain shown in the address bar of each open browser window of the signed-in user
    public const string DesktopLayoutReset = "desktop.layout.reset";

    public const string FsDeliver = "fs.deliver";   // copy an uploaded file into the user's Desktop/Documents/... as the user
    public const string FsFind = "fs.find";         // list files in a (token-expanded) folder as the user
    public const string AppearanceCatalog = "appearance.catalog"; // the settings this computer supports, each with its current value (1.21)
    public const string AppearanceGet = "appearance.get";         // the current value of named settings (1.21)
    public const string AppearanceSet = "appearance.set";         // several settings together; the reply says which were applied (1.21)
    public const string AppearanceReset = "appearance.reset";     // named settings (or all) back to what they were before Classroom Manager changed them (1.21)
    public const string FsRecent = "fs.recent";     // files (and folders) the signed-in user changed in the watched folders in a time window (1.19, T-212)
    public const string FsDeliveredDelete = "fs.delivered.delete"; // undo a delivery only if size/hash still match

    public const string TermStart = "term.start";
    public const string TermWrite = "term.write";
    public const string TermRead = "term.read";
    public const string TermClose = "term.close";

    public const string WallpaperSet = "wallpaper.set";
    public const string WallpaperReset = "wallpaper.reset";
    public const string WallpaperDefault = "wallpaper.default";
    public const string WallpaperColor = "wallpaper.color";
    public const string WallpaperManagedList = "wallpaper.managed.list";
    public const string WallpaperManagedSet = "wallpaper.managed.set";
    public const string WallpaperManagedDelete = "wallpaper.managed.delete";
    public const string WallpaperManagedPreview = "wallpaper.managed.preview";
    public const string WallpaperRecentList = "wallpaper.recent.list";     // the "Recent images" of the signed-in user's Background settings, with small previews
    public const string WallpaperRecentRemove = "wallpaper.recent.remove"; // take images off that list, and optionally delete their files (see WallpaperHistory)
    public const string WindowsPreferencesSet = "windows.preferences.set";
    public const string WindowsPreferencesGet = "windows.preferences.get";
    public const string LanguageInstall = "windows.language.install";
    public const string WindowsThemesList = "windows.themes.list";
    public const string WindowsThemeSet = "windows.theme.set";
    public const string WindowsUpdateScan = "windows.update.scan";
    public const string WindowsUpdateInstall = "windows.update.install";
    public const string WindowsUpdatePause = "windows.update.pause";
    public const string WindowsUpdateResume = "windows.update.resume";
    public const string WindowsUpdateStatus = "windows.update.status";
    public const string WindowsUpdateRestart = "windows.update.restart";
    public const string WindowsUpdateTargetSet = "windows.update.target.set"; // set or remove the Windows version this computer moves to and stays on (see WindowsRelease)

    public const string SignInImageSet = "signin.image.set";         // machine-wide lock/sign-in screen picture from an uploaded image (system level)
    public const string SignInImageDefault = "signin.image.default"; // remove that picture so Windows shows its own again
    public const string LockScreenReset = "lockscreen.reset";        // the signed-in user's own lock screen back to Windows spotlight (Windows Home), or {restore} what it was
    public const string LockScreenSet = "lockscreen.set";            // an uploaded picture as the signed-in user's own lock screen, on every edition (1.13); browser.policy.* sent at user level then act on that user's own policies
    public const string ScreenSaverGet = "screensaver.get";     // current per-user settings plus the Windows and managed .scr files available
    public const string ScreenSaverSet = "screensaver.set";     // turn off, pick an available .scr, or install an uploaded one for the signed-in user
    public const string ScreenSaverReset = "screensaver.reset"; // restore the settings saved before Classroom Manager first changed them

    public const string BrowserPolicyGet = "browser.policy.get";         // whether the machine-wide browser policies below are on, off or partly set
    public const string BrowserPolicySet = "browser.policy.set";         // no new profiles / clear data on exit for Chrome, Edge and Firefox; false restores what was there
    public const string BrowserList = "browser.list";                     // the browsers installed, and (as the user) which one opens web links; see BrowserCatalog
    public const string BrowserDefaultSet = "browser.default.set";        // the browser that opens web links for every user from their next sign-in; "" stops it
    public const string AppAccountsList = "apps.accounts.list";           // accounts the signed-in user's apps are signed in to (OneDrive, Microsoft 365, Dropbox, Steam...); see AppAccounts
    public const string BrowserDataClear = "browser.data.clear";         // close the signed-in user's browsers and delete history, cookies and/or cache
    public const string BrowserProfilesList = "browser.profiles.list";   // the signed-in user's browser profiles
    public const string BrowserProfilesRemove = "browser.profiles.remove"; // close the browsers and delete every profile except each browser's first, or only the ones named in "ids" (1.16)

    public const string DisplayWake = "display.wake";           // stop a running screen saver and turn the display on for the signed-in user

    public const string RecycleBinEmpty = "recyclebin.empty";   // empty the signed-in user's Recycle Bin on every drive; cannot be undone
    public const string RecycleBinNameReset = "recyclebin.name.reset"; // give the signed-in user's Recycle Bin its Windows name back after it was renamed

    public const string KeyboardList = "keyboard.list"; // the signed-in user's keyboard layouts and the default one (see KeyboardLayouts)
    public const string KeyboardSet = "keyboard.set";   // add or remove one of the signed-in user's layouts, or choose the default

    public const string MouseSet = "mouse.set";
    public const string MouseReset = "mouse.reset";

    public const string FsRoots = "fs.roots";
    public const string FsList = "fs.list";
    public const string FsMkdir = "fs.mkdir";
    public const string FsDelete = "fs.delete";
    public const string FsRename = "fs.rename";
    public const string FsRead = "fs.read";
    public const string FsWrite = "fs.write";
    public const string StagingList = "staging.list";
    public const string StagingDelete = "staging.delete";

    public const string ProcList = "proc.list";     // running processes (system-wide)
    public const string ProcKill = "proc.kill";     // end task by pid
    public const string ProcCloseApps = "proc.close-apps"; // ask every Alt+Tab window in the signed-in user's session to close

    public const string InventoryGet = "inventory.get"; // hardware + installed-software snapshot

    public const string BlockHostsList = "block.hosts.list";   // read back the domains currently in the managed block (+ whether the hosts file has other, unmanaged entries)
    public const string BlockHostsSet = "block.hosts.set";     // replace the managed block of domains in the hosts file
    public const string BlockHostsClear = "block.hosts.clear"; // remove the managed block entirely
    public const string BlockHostsAdd = "block.hosts.add";     // add domains to the managed block and keep the rest; the only change to it a Teacher key may make
    public const string BlockHostsReset = "block.hosts.reset"; // replace permanent domains and disable/clear the exam overlay; preserves unmanaged hosts lines
    public const string BlockAppsSet = "block.apps.set";       // replace the list of process names the service kills on sight
    public const string BlockAppsClear = "block.apps.clear";   // stop enforcing an app blocklist
    public const string BlockAppsList = "block.apps.list";     // read the permanent app blocklist (excluding the exam overlay)
    public const string ExamModeSet = "block.exam.set";        // enable/update the separate exam website/app overlay
    public const string ExamModeClear = "block.exam.clear";    // disable only the exam overlay, preserving permanent blocks
    public const string ExamDevicesSet = "block.exam.devices"; // exam overlay device controls: mute audio, camera off, deny removable storage (all false restores)

    public const string AccountList = "account.list";
    public const string AccountCreate = "account.create";
    public const string AccountSetPassword = "account.setpassword";
    public const string AccountEnable = "account.enable";
    public const string AccountDisable = "account.disable";
    public const string AccountDelete = "account.delete";
    public const string UserSignIn = "user.signin";   // nobody signed in: verify the password, arm Windows' one-time automatic sign-in, restart
    public const string AccountIdentityList = "account.identity.list";
    public const string AccountIdentityRemove = "account.identity.remove";
    public const string AccountIdentityOpenSettings = "account.identity.open-settings";

    public const string RegistryList = "registry.list";               // subkeys + values under a hive/path
    public const string RegistrySetValue = "registry.setvalue";       // create/overwrite a value
    public const string RegistryDeleteValue = "registry.deletevalue";
    public const string RegistryDeleteKey = "registry.deletekey";     // recursive; refuses a hive root
    public const string RegistryExport = "registry.export";           // reg.exe export -> a .reg file in the staging folder
    public const string RegistryImport = "registry.import";           // reg.exe import an already-uploaded .reg file ("push a registry patch as a program")

    public const string TasksList = "tasks.list";             // Scheduled Tasks (machine-wide)
    public const string TasksSetEnabled = "tasks.setenabled";
    public const string TasksDelete = "tasks.delete";
    public const string TasksRun = "tasks.run";                // run once, right now

    public const string AutorunList = "autorun.list";          // registry Run/RunOnce + Startup folder + (system level) scheduled tasks, correlated with installed programs
    public const string AutorunRemove = "autorun.remove";       // unhook just the autorun mechanism (registry value / startup shortcut / task), see AutorunHandler

    /// <summary>Commands that only make sense in the signed-in user's session.</summary>
    public static bool IsUserOnly(string cmd) =>
        cmd is WallpaperSet or WallpaperReset or WallpaperDefault or WallpaperColor or
            WallpaperManagedList or WallpaperManagedSet or WallpaperManagedDelete or WallpaperManagedPreview or WindowsPreferencesSet or WindowsPreferencesGet or
            WindowsThemesList or WindowsThemeSet or MouseSet or MouseReset or LockSet or LockClear or
            ScreenSaverGet or ScreenSaverSet or ScreenSaverReset or DisplayWake or RecycleBinEmpty or RecycleBinNameReset or BrowserSites or
            BrowserDataClear or BrowserProfilesList or BrowserProfilesRemove or KeyboardList or KeyboardSet or WallpaperRecentList or WallpaperRecentRemove or LockScreenReset or LockScreenSet or AppAccountsList
            or AccountIdentityList or AccountIdentityRemove or AccountIdentityOpenSettings or MessageShow or MessageClear or
            WebsiteOpen or ProgramOpen or DesktopLayoutReset or ProcWindows or ProcCloseApps or FsDeliver or FsFind or FsRecent or FsDeliveredDelete or
            AppearanceCatalog or AppearanceGet or AppearanceSet or AppearanceReset;
}

/// <summary>Where a command runs on the student PC.</summary>
public static class Levels
{
    public const string System = "system"; // elevated: the client service (LocalSystem)
    public const string User = "user";     // standard: the helper in the signed-in user's session
}

public sealed class Request
{
    public string Cmd { get; set; } = "";
    public string Level { get; set; } = Levels.System;
    public JsonObject Args { get; set; } = new();
}

public sealed class Response
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public JsonObject Data { get; set; } = new();

    public static Response Success(JsonObject? data = null) => new() { Ok = true, Data = data ?? new JsonObject() };
    public static Response Fail(string error) => new() { Ok = false, Error = error };

    /// <summary>A refusal with its stable error id (T-134), which the Host shows with the message.</summary>
    public static Response Fail(ErrorInfo id, string error) =>
        new() { Ok = false, Error = error, Data = new JsonObject { [ErrorCatalog.PayloadKey] = id.Id } };
    public static Response Unsupported(string command) => new()
    {
        Ok = false,
        Error = "This feature is not supported by this Classroom Client. Update the client and try again.",
        Data = new JsonObject
        {
            ["code"] = "unsupported_command",
            [ErrorCatalog.PayloadKey] = ErrorCatalog.FeatureNeedsNewerClient.Id,
            ["command"] = command,
            ["requiredProtocol"] = Protocol.RequiredVersion(command),
        },
    };
}

/// <summary>UDP reply a client sends when it hears a discovery broadcast.</summary>
public sealed class DiscoveryReply
{
    public string MachineName { get; set; } = "";
    /// <summary>Opaque installation identity. Unlike the address, this survives DHCP changes.</summary>
    public string ClientId { get; set; } = "";
    /// <summary>Primary physical adapter address, also used for identity migration and Wake-on-LAN.</summary>
    public string Mac { get; set; } = "";
    public int TcpPort { get; set; } = Protocol.DefaultTcpPort;
    public string Version { get; set; } = Protocol.Version;
}
