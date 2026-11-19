// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Dispatches requests. In the service, user-level requests are forwarded to the user helper;
/// in the helper (forwarder == null) everything runs locally with the user's own token.
///
/// Safety: if the connecting host turns out to be running on this same computer (see
/// CommandServer, which resolves this from the TCP peer address), power actions other than
/// "cancel", locking the screen, self-update, ending a process, arming an app blocklist,
/// signing a user in remotely (it restarts the PC), clearing browser data or removing browser profiles, and disabling/deleting an account are refused - so a teacher who points their own host app at
/// itself (by mistake, or while testing on one machine) can't shut down, restart, lock, brick
/// their own update, kill their own tools, or lock themselves out of their own account on the
/// very PC the host is running on.
/// </summary>
sealed class CommandRouter
{
    readonly string _clientId;
    readonly IUserForwarder? _forwarder;
    readonly FileHandler _files;
    readonly StagingHandler _staging = new();
    readonly InstallHandler _install = new();
    readonly UpdateHandler _update = new();
    readonly ProgramsHandler _programs = new();
    readonly CommandHandler _commands = new();
    readonly PowerHandler _power = new();
    readonly DisplayPowerHandler _displayPower = new();
    readonly DesktopHandler _desktop = new();
    readonly ScreenSaverHandler _screenSaver = new();
    readonly DisplayWakeHandler _displayWake = new();
    readonly RecycleBinHandler _recycleBin = new();
    readonly BrowserHandler _browsers = new();
    readonly SignInImageHandler _signInImage = new();
    readonly LockScreenHandler _lockScreen = new();
    readonly UninstallHandler _uninstall = new();
    readonly RenameHandler _rename = new();
    readonly AppAccountsHandler _appAccounts = new();
    readonly SignInHandler _signIn = new();
    readonly WindowsPreferencesHandler _windowsPreferences = new();
    // Created on first use: it names a file in the signed-in user's folder, which only the per-user helper has.
    readonly Lazy<AppearanceHandler> _appearance = new(() => new AppearanceHandler(new RegistryAppearanceStore(), new WindowsAppearanceShell(),
        Path.Combine(ClientConfig.UserDir, "appearance-originals.json"), Environment.OSVersion.Version.Build, new DisplayConfirmDialog()));
    readonly KeyboardHandler _keyboard = new();
    readonly WindowsUpdateHandler _windowsUpdate = new();
    readonly LockHandler _lock = new();
    readonly MessageHandler _message = new();
    readonly LaunchHandler _launch = new();
    readonly DesktopLayoutHandler _desktopLayout = new();
    readonly DeliveryHandler _delivery = new();
    readonly Lazy<TerminalHandler> _terminal = new();
    readonly TimeHandler _time = new();
    readonly ProcessHandler _process = new();
    readonly InventoryHandler _inventory = new();
    readonly AccountHandler _accounts = new();
    readonly RegistryHandler _registry = new();
    readonly TasksHandler _tasks = new();
    readonly AutorunHandler _autorun = new();
    readonly BlockHandler _block;
    readonly KeyHandler? _key;   // service only; the per-user helper cannot change the key
    readonly KeyStore? _keyStore;
    readonly bool _legacyDiscovery;
    readonly bool _requireBinding;

    /// <summary>Set by the service once the TLS server exists: when its certificate ends, for the Host's report (T-189).</summary>
    public Func<DateTime?>? CertificateNotAfter { get; set; }
    readonly StagingOwnership _stagingOwners = new(
        ClientConfig.StagingDir, Path.Combine(ClientConfig.DataDir, "staging-owners.json"));

    /// <summary>The block-list handler, shared with the service's background watcher loop (see ClientService).</summary>
    public BlockHandler Block => _block;

    public CommandRouter(ClientConfig config, IUserForwarder? forwarder, BlockHandler? blockHandler = null, KeyStore? keys = null)
    {
        _clientId = config.ClientId;
        _legacyDiscovery = config.AllowLegacyDiscovery;
        _keyStore = keys;
        _requireBinding = config.RequireChannelBinding;
        _forwarder = forwarder;
        _files = new FileHandler(config);
        _block = blockHandler ?? new BlockHandler();
        _key = keys is null ? null : new KeyHandler(keys);
    }

    public async Task<Response> HandleAsync(Request req, bool sameMachine, CallerIdentity caller, CancellationToken ct)
    {
        if (req.Cmd == Cmd.Ping) return Ping(caller);

        // Access control: the role of the key that opened this connection decides what it may send.
        if (!CommandAccess.IsAllowed(caller.Role, req.Cmd))
        {
            SecurityEvents.Current?.Write("command-refused", ("key", caller.KeyName), ("role", caller.Role.ToString()), ("command", req.Cmd!));
            return Forbidden(CommandAccess.Refusal(req.Cmd, caller.Role, caller.KeyName), req.Cmd);
        }
        // A transfer or a long job keeps the computer from sleeping until it ends (a Wake-on-LAN computer sleeps again after two minutes).
        using var awake = SleepGuard.Enter(req.Cmd);
        if (caller.Role < OperatorRole.Admin && req.Cmd == Cmd.FsWrite &&
            !_stagingOwners.MayWrite(caller.KeyName, req.Args["path"]?.GetValue<string>()))
            return Forbidden($"Not allowed with this key: '{caller.KeyName}' is a {caller.Role} key and can upload only into its own staging folder.", req.Cmd, ErrorCatalog.OwnStagingOnly);
        if (caller.Role < OperatorRole.Admin && !MayRemoveStaged(caller, req))
            return Forbidden($"Not allowed with this key: '{caller.KeyName}' is a {caller.Role} key and can list and delete only what it uploaded itself.", req.Cmd, ErrorCatalog.OwnStagingOnly);

        if (RefuseOnSameMachine(req, sameMachine) is { } refused) return refused;

        if (_forwarder is not null && WantsUserLevel(req))
            return await _forwarder.ForwardAsync(req, ct);

        return await HandleLocalAsync(req, caller, ct);
    }

    /// <summary>
    /// A key below Admin may delete, and list for deleting, only inside a staging folder it created:
    /// the files its own Host uploaded for Send files, Set wallpaper and the like.
    /// </summary>
    bool MayRemoveStaged(CallerIdentity caller, Request req) => req.Cmd switch
    {
        Cmd.FsDelete => _stagingOwners.Owns(caller.KeyName, req.Args["path"]?.GetValue<string>()),
        // Any file name below the Host's folder stands for that folder.
        Cmd.StagingList or Cmd.StagingDelete => _stagingOwners.Owns(caller.KeyName,
            Path.Combine(ClientConfig.StagingDir, "Hosts", req.Args["namespace"]?.GetValue<string>() ?? "", "file")),
        _ => true,
    };

    static Response Forbidden(string message, string command, ErrorInfo? error = null) => new()
    {
        Ok = false,
        Error = message,
        Data = new JsonObject
        {
            ["code"] = "forbidden",
            [ErrorCatalog.PayloadKey] = (error ?? ErrorCatalog.NotAllowedForRole).Id,
            ["command"] = command,
            ["requiredRole"] = CommandAccess.RequiredRole(command).ToString(),
        },
    };

    /// <summary>The security id of the user signed in at the computer, read from their session; null when nobody is.</summary>
    string? SignedInUserSid() => _forwarder?.CurrentSession is { } session ? SessionUser.Sid(session) : null;

    static bool WantsUserLevel(Request req) =>
        req.Level == Levels.User
        || Cmd.IsUserOnly(req.Cmd)
        || (req.Cmd == Cmd.PowerAction && req.Args["action"]?.GetValue<string>() == "logoff");

    /// <summary>The same-machine safety guard: the refusal for a blocked command from this computer itself, or null to go on.</summary>
    internal static Response? RefuseOnSameMachine(Request req, bool sameMachine) =>
        sameMachine && IsBlockedOnSameMachine(req)
            ? Response.Fail(ErrorCatalog.SameComputer,
                $"Refused ({req.Cmd}): the host and this client are the same computer. Actions that could " +
                "shut down, lock, or damage the teacher's own PC are blocked here - run this against a " +
                "student computer instead.")
            : null;

    /// <summary>Commands too risky to run when the host is talking to itself (see class summary).
    /// "cancel" (undo a pending shutdown/restart) and lock.clear (undo a lock) stay allowed.</summary>
    internal static bool IsBlockedOnSameMachine(Request req) => req.Cmd switch
    {
        Cmd.PowerAction => !string.Equals(req.Args["action"]?.GetValue<string>(), "cancel", StringComparison.OrdinalIgnoreCase),
        Cmd.LockSet => true,
        Cmd.SelfUpdate => true,
        Cmd.ClientUninstall => true,    // would remove the client from your own PC
        Cmd.SystemRename => true,       // would change the name of the PC the Host runs on
        Cmd.ProcKill => true,        // could end the very client/host process you're using to send this
        Cmd.ProcCloseApps => true,   // could close the Host UI when Host and Client share a computer
        Cmd.BlockAppsSet => true,    // the watcher it arms would then be free to kill your own control PC's processes
        Cmd.AccountDisable => true,
        Cmd.AccountDelete => true,
        Cmd.RegistryDeleteKey => true,
        Cmd.RegistryImport => true,   // an unvetted .reg patch applied to your own management PC
        Cmd.TasksDelete => true,
        Cmd.AutorunRemove => true,    // could unhook a security tool or the client itself on your own PC
        Cmd.UserSignIn => true,       // restarts the computer and arms an automatic sign-in on your own PC
        Cmd.BrowserDataClear => true,       // would close your own browsers and wipe your own history and sign-ins
        Cmd.BrowserProfilesRemove => true,  // would delete your own browser profiles, bookmarks and saved passwords
        Cmd.WallpaperRecentRemove => true,  // can delete your own picture files
        _ => false,
    };

    Response Ping(CallerIdentity caller)
    {
        bool helper = _forwarder is null || _forwarder.CurrentUser is not null;
        return Response.Success(new JsonObject
        {
            ["machineName"] = Environment.MachineName,
            ["userName"] = _forwarder is null ? Environment.UserName : _forwarder.CurrentUser ?? "",
            ["userAgent"] = helper,
            ["clientId"] = _clientId,
            ["mac"] = NetInfo.PrimaryMac(),
            ["version"] = Protocol.Version,
            ["protocolMajor"] = Protocol.Major,
            ["protocolMinor"] = Protocol.Minor,
            ["capabilities"] = new JsonArray(Protocol.Capabilities.Select(c => (JsonNode)c).ToArray()),
            ["role"] = caller.Role.ToString(),                    // what the key used for this connection may do
            ["desktopRuntime"] = InstalledDesktopRuntime(),       // newest .NET Desktop Runtime on this PC, "" when none
            ["keyName"] = caller.KeyName,
            ["appVersion"] = AppVersion(),                        // build/file version - what self.update replaces
            ["windowsVersion"] = WindowsVersion(),
            ["interfaceLanguage"] = CultureInfo.InstalledUICulture.Name,
            ["blockedWebsiteCount"] = _block.BlockedWebsiteCount,
            ["utcTicks"] = DateTime.UtcNow.Ticks,                 // lets the host measure clock drift
            ["uptimeSec"] = Environment.TickCount64 / 1000,
            ["security"] = SecurityEvents.Current?.Snapshot() ?? new JsonObject(),   // counters and the end of the event chain (T-191)
            // The settings that decide how much a stranger can learn or relay; the Host's "secure this lab" report reads them (T-194).
            ["architecture"] = PeArchitecture.Current,                                    // x86 or x64: what an update must be built for
            ["windowsArchitecture"] = Environment.Is64BitOperatingSystem ? "x64" : "x86",
            ["keyEpoch"] = _keyStore?.Epoch ?? 0,
            ["certificateNotAfterUtc"] = CertificateNotAfter?.Invoke()?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["room"] = _keyStore?.Room ?? "",
            ["keyChangedUtc"] = _keyStore?.PrimaryChanged?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),   // key age for the Host's lab report (T-189)
            ["hardening"] = new JsonObject { ["legacyDiscovery"] = _legacyDiscovery, ["requireChannelBinding"] = _requireBinding },
        });
    }

    /// <summary>
    /// The newest released .NET Desktop Runtime installed for all users, whether or not this build
    /// uses it. The Host needs it before pushing a build that does (see "Update client").
    /// </summary>
    static string InstalledDesktopRuntime()
    {
        try
        {
            var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(shared)) return "";
            // Preview builds are named like 10.0.0-rc.1 and do not parse as a version, so they are skipped.
            return Directory.EnumerateDirectories(shared)
                .Select(directory => Version.TryParse(Path.GetFileName(directory), out var version) ? version : null)
                .Where(version => version is not null)
                .Max()?.ToString() ?? "";
        }
        catch { return ""; }
    }

    static string AppVersion()
    {
        try
        {
            var path = Environment.ProcessPath;
            return path is null ? "" : FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
        }
        catch { return ""; }
    }

    static string WindowsVersion()
    {
        try
        {
            var version = Environment.OSVersion.Version;
            return $"Windows {version.Major}.{version.Minor} (build {version.Build})";
        }
        catch { return ""; }
    }

    async Task<Response> HandleLocalAsync(Request req, CallerIdentity caller, CancellationToken ct)
    {
        var a = req.Args;
        // The helper's block handler has no lists of its own (T-160); only the service's may answer.
        if (!_block.Persisted && BlockHandler.Handles(req.Cmd))
            return Response.Fail("Websites and programs are blocked by the client service.");
        switch (req.Cmd)
        {
            case Cmd.InstallRun: return await _install.RunInstallerAsync(a, ct);
            case Cmd.InstallWinget: return await _install.WingetAsync(a, ct);
            case Cmd.SelfUpdate: return await _update.ApplyAsync(a, ct);
            case Cmd.ClientUninstall: return _uninstall.Start(a);
            case Cmd.SystemRename: return _rename.Rename(a);
            case Cmd.KeySet: return _key is null ? Response.Fail("The shared key can only be changed by the client service.") : _key.Set(a);
            case Cmd.AccessKeysList: return _key is null ? Response.Fail("Access keys are managed by the client service.") : _key.ListAccessKeys();
            case Cmd.AccessKeysSet: return _key is null ? Response.Fail("Access keys are managed by the client service.") : _key.SetAccessKey(a, caller);
            case Cmd.AccessKeysRemove: return _key is null ? Response.Fail("Access keys are managed by the client service.") : _key.RemoveAccessKey(a, caller);

            case Cmd.ProgramsList: return _programs.List();
            case Cmd.ProgramsUninstall: return await _programs.UninstallAsync(a, ct);

            case Cmd.CmdRun: return await _commands.RunAsync(a, ct);
            case Cmd.PowerAction: return await _power.RunAsync(a, ct);
            case Cmd.PowerDisplayGet: return _displayPower.Get();
            case Cmd.PowerDisplaySet: return _displayPower.Set(a);
            case Cmd.TimeSync: return await _time.SyncAsync(a, ct);

            case Cmd.WallpaperSet: return _desktop.SetWallpaper(a);
            case Cmd.WallpaperReset: return _desktop.ResetWallpaper();
            case Cmd.WallpaperDefault: return _desktop.SetDefaultWallpaper();
            case Cmd.WallpaperColor: return _desktop.SetSolidColor(a);
            case Cmd.WallpaperManagedList: return _desktop.ListManagedWallpapers();
            case Cmd.WallpaperManagedSet: return _desktop.SetManagedWallpaper(a);
            case Cmd.WallpaperManagedDelete: return _desktop.DeleteManagedWallpapers(a);
            case Cmd.WallpaperManagedPreview: return _desktop.PreviewManagedWallpaper(a);
            case Cmd.WallpaperRecentList: return _desktop.ListRecentWallpapers();
            case Cmd.WallpaperRecentRemove: return _desktop.RemoveRecentWallpapers(a);
            case Cmd.AppearanceCatalog: return _appearance.Value.Catalog();
            case Cmd.AppearanceGet: return _appearance.Value.Get(a);
            case Cmd.AppearanceSet: return _appearance.Value.Set(a);
            case Cmd.AppearanceReset: return _appearance.Value.Reset(a);
            case Cmd.WindowsPreferencesGet: return _windowsPreferences.Get();
            case Cmd.KeyboardList: return await _keyboard.ListAsync(ct);
            case Cmd.KeyboardSet: return await _keyboard.SetAsync(a, ct);
            case Cmd.WindowsPreferencesSet: return await _windowsPreferences.SetAsync(a, ct);
            case Cmd.LanguageInstall: return await _windowsPreferences.InstallLanguageAsync(a, ct);
            case Cmd.WindowsThemesList: return _windowsPreferences.ListThemes();
            case Cmd.WindowsThemeSet: return await _windowsPreferences.ApplyThemeAsync(a, ct);
            case Cmd.WindowsUpdateScan: return await _windowsUpdate.ScanAsync(a, ct);
            case Cmd.WindowsUpdateInstall: return await _windowsUpdate.InstallAsync(a, ct);
            case Cmd.WindowsUpdatePause: return _windowsUpdate.Pause(a);
            case Cmd.WindowsUpdateResume: return _windowsUpdate.Resume();
            case Cmd.WindowsUpdateStatus: return _windowsUpdate.Status();
            case Cmd.WindowsUpdateRestart: return _windowsUpdate.Restart();
            case Cmd.WindowsUpdateTargetSet: return _windowsUpdate.SetTarget(a);
            case Cmd.SignInImageSet: return await _signInImage.SetAsync(a, ct);
            case Cmd.SignInImageDefault: return _signInImage.UseDefault();
            case Cmd.LockScreenReset: return _lockScreen.Reset(a);
            case Cmd.LockScreenSet: return _lockScreen.Set(a);
            case Cmd.ScreenSaverGet: return _screenSaver.Get();
            case Cmd.ScreenSaverSet: return await _screenSaver.SetAsync(a, ct);
            case Cmd.ScreenSaverReset: return _screenSaver.Reset();
            case Cmd.DisplayWake: return await _displayWake.WakeAsync(ct);
            case Cmd.MouseSet: return _desktop.SetMouse(a);
            case Cmd.MouseReset: return _desktop.ResetMouse();
            case Cmd.RecycleBinEmpty: return _recycleBin.Empty();
            case Cmd.RecycleBinNameReset: return _recycleBin.ResetName();

            case Cmd.LockSet: return await _lock.SetAsync(a);
            case Cmd.LockClear: return await _lock.ClearAsync();

            case Cmd.MessageShow: return await _message.ShowAsync(a);
            case Cmd.MessageClear: return await _message.ClearAsync();
            case Cmd.WebsiteOpen: return await _launch.OpenWebsiteAsync(a, ct);
            case Cmd.ProgramOpen: return _launch.OpenProgram(a);
            case Cmd.DesktopLayoutReset: return await _desktopLayout.ResetAsync(a, ct);

            case Cmd.FsDeliver: return _delivery.Deliver(a);
            case Cmd.FsFind: return _delivery.Find(a);
            // A scan of a large Documents folder takes a few seconds; it runs off the connection's thread.
            case Cmd.FsRecent: return await Task.Run(() => DeliveryHandler.Recent(a, DateTime.UtcNow), ct);
            case Cmd.FsDeliveredDelete: return _delivery.DeleteDelivered(a);

            case Cmd.TermStart: return _terminal.Value.Start(a);
            case Cmd.TermWrite: return await _terminal.Value.WriteAsync(a);
            case Cmd.TermRead: return _terminal.Value.Read(a);
            case Cmd.TermClose: return _terminal.Value.Close(a);

            case Cmd.FsRoots: return _files.Roots();
            case Cmd.FsList: return _files.List(a);
            case Cmd.FsMkdir: return _files.Mkdir(a);
            case Cmd.FsDelete: return _files.Delete(a);
            case Cmd.FsRename: return _files.Rename(a);
            case Cmd.FsRead: return await _files.ReadAsync(a, ct);
            case Cmd.FsWrite: return await _files.WriteAsync(a, ct);
            case Cmd.StagingList: return _staging.List(a);
            case Cmd.StagingDelete: return _staging.Delete(a);

            case Cmd.ProcList: return _process.List();
            case Cmd.ProcWindows: return _process.Windows();
            case Cmd.BrowserSites: return _process.Sites();
            case Cmd.BrowserPolicyGet:
            case Cmd.BrowserPolicySet:
                // forUser (1.13): the signed-in user's own policies, written by the service because users may only read theirs.
                string? userSid = null;
                if (a.Bool(BrowserRules.ForUser) == true && (userSid = SignedInUserSid()) is null)
                    return Response.Fail("Nobody is signed in, so there is no user to set browser policies for.");
                return req.Cmd == Cmd.BrowserPolicyGet ? _browsers.PolicyGet(userSid) : _browsers.PolicySet(a, userSid);
            case Cmd.BrowserList: return _browsers.List();
            case Cmd.BrowserDefaultSet: return _browsers.DefaultSet(a);
            case Cmd.AppAccountsList: return _appAccounts.List();
            case Cmd.BrowserDataClear: return await _browsers.DataClearAsync(a, ct);
            case Cmd.BrowserProfilesList: return _browsers.ProfilesList();
            case Cmd.BrowserProfilesRemove: return await _browsers.ProfilesRemoveAsync(a, ct);
            case Cmd.ProcCloseApps: return _process.CloseApps();
            case Cmd.ProcKill: return _process.Kill(a);

            case Cmd.InventoryGet: return _inventory.Get();

            case Cmd.BlockHostsList: return _block.ListHosts();
            case Cmd.BlockHostsSet: return _block.SetHosts(a);
            case Cmd.BlockHostsAdd: return _block.AddHosts(a);
            case Cmd.BlockHostsClear: return _block.ClearHosts();
            case Cmd.BlockHostsReset: return _block.ResetHosts(a);
            case Cmd.BlockAppsSet: return _block.SetApps(a);
            case Cmd.BlockAppsClear: return _block.ClearApps();
            case Cmd.BlockAppsList: return _block.ListApps();
            case Cmd.ExamModeSet: return _block.SetExamMode(a);
            case Cmd.ExamModeClear: return _block.ClearExamMode();
            case Cmd.ExamDevicesSet: return _block.SetExamDevices(a);

            case Cmd.AccountList: return _accounts.List();
            case Cmd.AccountCreate: return _accounts.Create(a);
            case Cmd.AccountSetPassword: return _accounts.SetPassword(a);
            case Cmd.AccountEnable: return _accounts.SetEnabled(a, true);
            case Cmd.AccountDisable: return _accounts.SetEnabled(a, false);
            case Cmd.AccountDelete: return _accounts.Delete(a);
            case Cmd.UserSignIn: return await _signIn.RequestAsync(a, ct);
            case Cmd.AccountIdentityList: return _accounts.IdentityList();
            case Cmd.AccountIdentityRemove: return _accounts.IdentityRemove(a);
            case Cmd.AccountIdentityOpenSettings: return _accounts.OpenIdentitySettings();

            case Cmd.RegistryList: return _registry.List(a);
            case Cmd.RegistrySetValue: return _registry.SetValue(a);
            case Cmd.RegistryDeleteValue: return _registry.DeleteValue(a);
            case Cmd.RegistryDeleteKey: return _registry.DeleteKey(a);
            case Cmd.RegistryExport: return await _registry.ExportAsync(a, ct);
            case Cmd.RegistryImport: return await _registry.ImportAsync(a, ct);

            case Cmd.TasksList: return await _tasks.ListAsync(ct);
            case Cmd.TasksSetEnabled: return await _tasks.SetEnabledAsync(a, ct);
            case Cmd.TasksDelete: return await _tasks.DeleteAsync(a, ct);
            case Cmd.TasksRun: return await _tasks.RunAsync(a, ct);

            case Cmd.AutorunList: return await _autorun.ListAsync(ct);
            case Cmd.AutorunRemove: return await _autorun.RemoveAsync(a, ct);

            default: return Response.Unsupported(req.Cmd);
        }
    }
}

static class ArgExt
{
    public static string Str(this JsonObject a, string key, string? fallback = null)
    {
        var v = a[key]?.GetValue<string>();
        if (v is null)
        {
            if (fallback is not null) return fallback;
            throw new ArgumentException($"Missing argument '{key}'.");
        }
        return v;
    }

    public static int? Int(this JsonObject a, string key) => a[key]?.GetValue<int>();
    public static long Long(this JsonObject a, string key, long fallback = 0) => a[key]?.GetValue<long>() ?? fallback;
    public static bool? Bool(this JsonObject a, string key) => a[key]?.GetValue<bool>();
}
