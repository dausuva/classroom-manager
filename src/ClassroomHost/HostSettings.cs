// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class HostSettings
{
    /// <summary>Stable IDs disabled by the owner. Missing preferences preserve existing enabled behavior.</summary>
    public List<string> DisabledModules { get; set; } = new();

    [JsonIgnore]
    public string Psk { get; set; } = "";

    /// <summary>Friendly name shown for the original/default shared key.</summary>
    public string DefaultKeyName { get; set; } = "Default lab";

    /// <summary>Independent keys for other labs. These are not key-rotation fallback keys.</summary>
    public List<NamedHostKey> AdditionalHostKeys { get; set; } = new();

    /// <summary>
    /// Keys from before a rotation ("Rotate key..."), still tried when the current key is refused, so
    /// computers that missed the change stay reachable and get the new key the next time they are seen.
    /// Emptied automatically once every computer in the list has confirmed the current key.
    /// </summary>
    [JsonIgnore]
    public List<string> PreviousPsks { get; set; } = new();

    /// <summary>How many times the lab key was rotated from this Host (T-189). Sent with the key so the report can tell which computers are behind.</summary>
    public int KeyEpoch { get; set; }

    /// <summary>When the last rotation started (UTC); the grace window for computers that were off counts from here.</summary>
    public DateTime? KeyEpochChangedUtc { get; set; }

    /// <summary>Days a computer may stay on the key from before the last rotation before the report calls it expired and a person must deal with it.</summary>
    public int KeyGraceDays { get; set; } = 14;

    /// <summary>Access keys that were removed while a computer was off; the removal is finished when it next answers (see <see cref="KeyRevocations"/>).</summary>
    public List<PendingRevocation> PendingRevocations { get; set; } = new();

    public void StartNewKeyEpoch(DateTime nowUtc)
    {
        KeyEpoch++;
        KeyEpochChangedUtc = nowUtc;
    }

    /// <summary>Push the current key to a computer found still using a previous key (status checks).</summary>
    public bool AutoFinishKeyRotation { get; set; } = true;

    /// <summary>Short random id of this host; its uploads go to their own staging subfolder so two hosts never overwrite each other.</summary>
    public string HostId { get; set; } = "";

    /// <summary>Opaque staging namespace component; stored only in the DPAPI-protected Host secret store.</summary>
    [JsonIgnore]
    public string UploadToken { get; set; } = "";

    /// <summary>Turn off on the second host (e.g. the laptop) so scheduled actions do not run twice.</summary>
    public bool RunSchedules { get; set; } = true;

    /// <summary>
    /// Also run schedules while the Host is closed, through a Windows Task Scheduler task of this Windows user (see BackgroundSchedules).
    /// Off by default.
    /// </summary>
    public bool RunSchedulesWhenClosed { get; set; }

    /// <summary>Which sample schedules this settings file has been given (each once, so one the teacher deleted stays deleted).</summary>
    public int ScheduleDefaultsVersion { get; set; }

    public int TcpPort { get; set; } = Protocol.DefaultTcpPort;
    public int UdpPort { get; set; } = Protocol.DefaultUdpPort;

    /// <summary>
    /// IPv4 subnet in CIDR notation used for client discovery. Empty means automatic selection,
    /// preferring a physical Ethernet LAN and then Wi-Fi over VPN or virtual interfaces.
    /// </summary>
    public string DiscoverySubnet { get; set; } = "";

    /// <summary>
    /// Always true for a Host that was loaded from disk (T-186): TLS cannot be turned off. The property remains so that tests can
    /// build a Host that talks to a plain test server; <see cref="Load"/> ignores what a file says.
    /// </summary>
    public bool UseTls { get; set; } = true;

    /// <summary>
    /// Add a computer and approve its certificate by itself when it announces them in answer to this Host's signed discovery probe (T-185,
    /// enrolment mode: only a newly installed Client does, only to a holder of the lab key, and only for an address with no approval yet).
    /// </summary>
    public bool AutoEnrolNewComputers { get; set; } = true;

    /// <summary>True when the file that was loaded said TLS was off; that was ignored.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool TlsWasOff { get; set; }
    public List<TrustedTlsEndpoint> TrustedTlsEndpoints { get; set; } = [];

    /// <summary>
    /// The approved certificates for a computer. With its network card address: what was approved for that card,
    /// wherever it was then, else what is approved for the address unless that belongs to another card. Without
    /// it, only what is approved for the address.
    /// </summary>
    public IReadOnlyList<string> TlsPins(string address, int port, string? mac = null)
    {
        var all = (TrustedTlsEndpoints ?? []).Where(endpoint => endpoint.Port == port).ToList();
        var card = ComputerIdentity.NormalizeMac(mac);
        string[] Pins(IEnumerable<TrustedTlsEndpoint> endpoints) =>
            endpoints.SelectMany(endpoint => endpoint.CertificateSha256 ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (card.Length > 0)
        {
            var owned = Pins(all.Where(endpoint => ComputerIdentity.NormalizeMac(endpoint.Mac) == card));
            if (owned.Length > 0) return owned;
        }
        // An approval for this address that was made for another card (its computer has moved on) is not this one's.
        var others = card.Length == 0 ? [] : Pins(all.Where(endpoint => ComputerIdentity.NormalizeMac(endpoint.Mac) is { Length: > 0 } other && other != card));
        return Pins(all.Where(endpoint => endpoint.Address.Trim().Equals(address.Trim(), StringComparison.OrdinalIgnoreCase)
                                          && (card.Length == 0 || ComputerIdentity.NormalizeMac(endpoint.Mac) is not { Length: > 0 } other || other == card)))
            .Where(pin => !others.Contains(pin, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>
    /// Whether this computer already has an approval, however damaged: usable certificates, or an entry for it that
    /// holds none (which is never an invitation to enrol a new one over it). An approval that the address holds for
    /// another network card does not count.
    /// </summary>
    public bool HasTrustEntry(string address, int port, string? mac)
    {
        if (TlsPins(address, port, mac).Count > 0) return true;
        var card = ComputerIdentity.NormalizeMac(mac);
        return (TrustedTlsEndpoints ?? []).Any(endpoint => endpoint.Port == port && (endpoint.CertificateSha256 ?? []).Count == 0 &&
            (card.Length > 0 && ComputerIdentity.NormalizeMac(endpoint.Mac) == card ||
             endpoint.Address.Trim().Equals(address.Trim(), StringComparison.OrdinalIgnoreCase) &&
             (card.Length == 0 || endpoint.Mac.Length == 0 || ComputerIdentity.NormalizeMac(endpoint.Mac) == card)));
    }

    /// <summary>
    /// Writes down whose the approval at an address is, once that computer has answered there over the approved
    /// certificate. Only entries that do not name a card yet are changed. Returns whether anything changed.
    /// </summary>
    public bool ClaimTrust(string address, int port, string? mac)
    {
        var card = ComputerIdentity.NormalizeMac(mac);
        if (card.Length == 0) return false;
        var changed = false;
        foreach (var endpoint in (TrustedTlsEndpoints ?? []).Where(endpoint => endpoint.Port == port
                     && endpoint.Mac.Length == 0 && endpoint.Address.Trim().Equals(address.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            endpoint.Mac = card;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Approves these certificates (one or two) for a computer, replacing only that computer's own approvals and the
    /// unclaimed ones at its address. What the address holds for another network card stays: that computer may be there.
    /// </summary>
    public void EnrollTrust(string address, int port, string? mac, IEnumerable<string> pins)
    {
        var card = ComputerIdentity.NormalizeMac(mac);
        var list = TrustedTlsEndpoints ??= [];
        list.RemoveAll(endpoint => endpoint.Port == port &&
            (card.Length > 0 && ComputerIdentity.NormalizeMac(endpoint.Mac) == card ||
             endpoint.Address.Trim().Equals(address.Trim(), StringComparison.OrdinalIgnoreCase) &&
             (card.Length == 0 || endpoint.Mac.Length == 0)));
        var approved = pins.Select(pin => pin.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (approved.Count > 0) list.Add(new TrustedTlsEndpoint { Address = address.Trim(), Port = port, Mac = card, CertificateSha256 = approved });
    }

    /// <summary>A computer that moved to another address takes its approvals with it. Returns whether anything changed.</summary>
    public bool MoveTrust(string? mac, string newAddress, int port)
    {
        var card = ComputerIdentity.NormalizeMac(mac);
        if (card.Length == 0) return false;
        var changed = false;
        var list = TrustedTlsEndpoints ??= [];
        foreach (var endpoint in list.Where(endpoint => endpoint.Port == port && ComputerIdentity.NormalizeMac(endpoint.Mac) == card
                     && !endpoint.Address.Trim().Equals(newAddress.Trim(), StringComparison.OrdinalIgnoreCase)).ToList())
        {
            // An entry the new address already had stays: nothing a person approved is thrown away.
            endpoint.Address = newAddress.Trim();
            changed = true;
        }
        return changed;
    }

    /// <summary>"Light" (the default), "System" (follows Windows) or "Dark" (see AppTheme). Applied at startup and immediately when changed in Settings.</summary>
    public string Theme { get; set; } = "Light";

    /// <summary>"System" (the language of Windows) or a language code such as "lt-LT" (see Loc and UiTranslator).</summary>
    public string Language { get; set; } = Loc.Windows;

    /// <summary>"IconsOnly", "NamesOnly", or "IconsAndNames" (see ButtonDisplayMode).</summary>
    public string ButtonDisplay { get; set; } = "IconsAndNames";

    /// <summary>Write unhandled Host exceptions to the local crash log for troubleshooting.</summary>
    public bool CrashLoggingEnabled { get; set; } = true;

    /// <summary>Also write every line of the Log view to a daily file (see ActivityLogFile).</summary>
    public bool ActivityLogEnabled { get; set; } = true;

    /// <summary>Days a daily log file is kept; one of ActivityLogFile.RetentionChoices.</summary>
    public int ActivityLogKeepDays { get; set; } = ActivityLogFile.KeepDays;

    /// <summary>Look once a day whether a newer version has been published (see UpdateNotice). Nothing is downloaded.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>The welcome notice of a new Host has been shown (FirstLaunchNotice); it is shown once.</summary>
    public bool FirstLaunchNoticeShown { get; set; }
    public string LastUpdateCheckUtc { get; set; } = "";

    /// <summary>Refresh assigned computer status automatically while the Host is open.</summary>
    public bool AutoRefreshEnabled { get; set; } = true;

    /// <summary>
    /// Computer-list columns hidden by the user. Storing the exceptions keeps newly introduced
    /// columns visible by default after an upgrade.
    /// </summary>
    public List<string> HiddenComputerColumns { get; set; } = new();
    public List<string> HiddenProcessColumns { get; set; } = new();

    /// <summary>Recent files (T-212): watched folders per computer, file type groups, where copies go and how long they stay.</summary>
    public RecentFilesSettings RecentFiles { get; set; } = new();

    /// <summary>Host-side exam overlay configuration. Clients persist the active overlay independently.</summary>
    public bool ExamModeEnabled { get; set; }

    /// <summary>Keep the explanation of every window open instead of folding it behind an info icon after a few openings.</summary>
    public bool AlwaysShowExplanations { get; set; }

    /// <summary>How many times each window (by type name) has been opened, so its explanation folds after the first few times.</summary>
    public Dictionary<string, int> WindowOpenings { get; set; } = new(StringComparer.Ordinal);
    public List<string> ExamModeDomains { get; set; } = new();
    public List<string> ExamModeApps { get; set; } = new();
    /// <summary>Exam overlay device controls; they need Client protocol 1.8.</summary>
    public bool ExamModeMuteAudio { get; set; }
    public bool ExamModeDisableCamera { get; set; }
    public bool ExamModeBlockUsbStorage { get; set; }
    /// <summary>The computers Exam mode was turned on for (see <see cref="ExamModeScope"/>).</summary>
    public List<string> ExamModeComputers { get; set; } = new();
    /// <summary>Computers that were unreachable when Exam mode was turned off; cleared when they answer again.</summary>
    public List<string> ExamModePendingClear { get; set; } = new();
    public List<string> BlockedAppCatalog { get; set; } = new();
    public List<string> DefaultWebsiteBlocklist { get; set; } = new();

    public List<string> SavedWebsites { get; set; } = new();
    public List<string> RecentWebsites { get; set; } = new();
    /// <summary>Open website: ask for a maximized window (protocol 1.15). On unless the teacher turns it off.</summary>
    public bool OpenWebsiteMaximized { get; set; } = true;
    /// <summary>Open website: the browser to use, a BrowserCatalog id; empty is the default browser of each computer.</summary>
    public string OpenWebsiteBrowser { get; set; } = "";
    public List<SavedProgramLaunch> SavedPrograms { get; set; } = new();
    public List<SavedProgramLaunch> RecentPrograms { get; set; } = new();
    public Dictionary<string, WindowFilterSettings> WindowFilters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Folders on this computer that collected or synced files went into, newest first.</summary>
    public List<string> RecentCollectFolders { get; set; } = new();

    /// <summary>Websites noted to block later; nothing here reaches a computer until it is blocked in Block websites.</summary>
    public List<WebsiteDraft> DraftWebsites { get; set; } = new();

    public List<string> QuickActions { get; set; } = new() { "lock", "unlock", "send-files" };
    public string QuickActionMessage { get; set; } = "Please look at the teacher.";
    /// <summary>User-created buttons in the Actions section, in their chosen order.</summary>
    public List<CustomQuickAction> CustomQuickActions { get; set; } = new();
    /// <summary>How the teacher arranged the main toolbar, per tab; a tab not here is as the program lays it out.</summary>
    public Dictionary<string, ToolbarLayout> ToolbarLayouts { get; set; } = new();
    public int ToolbarLayoutVersion { get; set; }
    /// <summary>The keyboard layouts every computer should have and the default one; null until saved from Keyboard layouts.</summary>
    public KeyboardRoomDefault? KeyboardRoomDefault { get; set; }
    public int QuickActionDefaultsVersion { get; set; }

    public List<FavoriteCommand> FavoriteCommands { get; set; } = new();

    public string HostMode { get; set; } = HostModes.Teacher;
    public List<string> TeacherVisibleCommands { get; set; } = TeacherCommandCatalog.Defaults();
    /// <summary>Which of TeacherCommandCatalog.AddedDefaults these settings already received.</summary>
    public int TeacherDefaultsVersion { get; set; }

    public List<SavedComputer> Computers { get; set; } = new();
    public List<ScheduledAction> Schedules { get; set; } = new();
    public List<WindowsUpdateSnapshot> WindowsUpdateSnapshots { get; set; } = new();

    /// <summary>The Appearance presets the teacher saved.</summary>
    public List<AppearancePresetRecord> AppearancePresets { get; set; } = new();
    public List<FileTransferRecord> FileTransferHistory { get; set; } = new();

    /// <summary>Legacy (v1) list of manually added addresses; migrated into Computers on load.</summary>
    public List<string> ManualClients { get; set; } = new();

    public bool HasHostKeys => !string.IsNullOrWhiteSpace(Psk) ||
                               AdditionalHostKeys.Any(x => !string.IsNullOrWhiteSpace(x.Psk));

    public IEnumerable<(string Name, string Psk, bool IsDefault)> CurrentHostKeys()
    {
        if (!string.IsNullOrWhiteSpace(Psk)) yield return (NormalizedDefaultKeyName(), Psk, true);
        foreach (var key in AdditionalHostKeys.Where(x => !string.IsNullOrWhiteSpace(x.Psk)))
            yield return (string.IsNullOrWhiteSpace(key.Name) ? "Unnamed lab" : key.Name.Trim(), key.Psk, false);
    }

    public string KeyNameFromFingerprint(string fingerprint)
    {
        if (fingerprint.Length == 0) return "";
        var current = CurrentHostKeys().FirstOrDefault(x =>
            SharedKey.Fingerprint(x.Psk).Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(current.Name)) return current.Name;
        return PreviousPsks.Any(x => SharedKey.Fingerprint(x).Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
            ? NormalizedDefaultKeyName() + " (old key)"
            : "";
    }

    public string NormalizedDefaultKeyName() =>
        string.IsNullOrWhiteSpace(DefaultKeyName) ? "Default lab" : DefaultKeyName.Trim();

    /// <summary>
    /// The folder that holds this Windows user's Host files: settings, keys, feedback, logs, wallpapers.
    /// Everything in the Host that reads or writes them goes through here, so the test project can
    /// point it at a scratch folder once and no test can ever touch a real person's settings.
    /// </summary>
    public static string DataFolder { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassroomManager");

    public static string PathOnDisk => System.IO.Path.Combine(DataFolder, "host.json");

    public static HostSettings Load(string? path = null)
    {
        path ??= PathOnDisk;
        path = System.IO.Path.GetFullPath(path);
        using var storageLease = HostSettingsCommit.Enter(path);
        try
        {
            if (File.Exists(path) || File.Exists(AtomicFile.BackupPath(path)))
            {
                var (s, json, recoveredFromBackup) = ReadSettings(path);
                s.TlsWasOff = !s.UseTls;
                s.UseTls = true;
                var legacySecrets = HostSecretStore.ReadLegacy(json, s.AdditionalHostKeys);
                var secretPath = HostSecretStore.PathFor(path);
                if (File.Exists(secretPath) || File.Exists(AtomicFile.BackupPath(secretPath)))
                    HostSecretStore.Apply(s, HostSecretStore.Load(secretPath, recoveredFromBackup));
                else HostSecretStore.Apply(s, legacySecrets.Secrets);
                foreach (var addr in s.ManualClients)
                    if (!s.Computers.Any(c => c.Address.Equals(addr, StringComparison.OrdinalIgnoreCase)))
                        s.Computers.Add(new SavedComputer { Address = addr, Name = addr });
                s.ManualClients.Clear();
                s.NormalizeHostKeys();
                s.NormalizeUiSettings();
                if (recoveredFromBackup) AtomicFile.RestoreFromBackup(path);
                // Read now: the saves below write what was just read, not someone else's settings.
                Remember(path);
                s.EnsureHostId(path);
                if (legacySecrets.ContainsSecrets)
                {
                    s.Save(path);
                    AtomicFile.RefreshBackup(path);
                }
                Remember(path);
                return s;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Could not load Host settings from {path}.", ex);
        }
        var fresh = new HostSettings();
        fresh.NormalizeHostKeys();
        fresh.NormalizeUiSettings();
        fresh.EnsureHostId(path);
        return fresh;
    }

    static (HostSettings Settings, string Json, bool RecoveredFromBackup) ReadSettings(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return (JsonSerializer.Deserialize<HostSettings>(json, Protocol.Json)
                    ?? throw new InvalidDataException("The Host settings file is empty."), json, false);
        }
        catch (Exception ex) when (ex is IOException or JsonException && File.Exists(AtomicFile.BackupPath(path)))
        {
            var json = File.ReadAllText(AtomicFile.BackupPath(path));
            return (JsonSerializer.Deserialize<HostSettings>(json, Protocol.Json)
                    ?? throw new InvalidDataException("The Host settings backup is empty."), json, true);
        }
    }

    void NormalizeHostKeys()
    {
        DefaultKeyName = NormalizedDefaultKeyName();
        AdditionalHostKeys ??= new();
        AdditionalHostKeys = AdditionalHostKeys
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Psk) && x.Psk != Psk)
            .GroupBy(x => x.Psk, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        foreach (var key in AdditionalHostKeys)
        {
            if (string.IsNullOrWhiteSpace(key.Id)) key.Id = Guid.NewGuid().ToString("N");
            key.Name = string.IsNullOrWhiteSpace(key.Name) ? "Unnamed lab" : key.Name.Trim();
        }
    }

    void EnsureHostId(string path)
    {
        var changed = false;
        if (HostId.Length == 0)
        {
            HostId = Guid.NewGuid().ToString("N")[..8];
            changed = true;
        }
        if (UploadToken.Length != 32)
        {
            UploadToken = Guid.NewGuid().ToString("N");
            changed = true;
        }
        if (changed) Save(path);
    }

    void NormalizeUiSettings()
    {
        var legacyColumnNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Signed-in user"] = "User",
            ["Clock vs host"] = "Clock",
            ["Windows"] = "OS Version",
            ["Interface language"] = "Interface",
            ["Blocked websites"] = "Hosts",
            ["Running apps / open windows"] = "Apps",
            ["Client build"] = "Client",
            ["Last seen"] = "Last Seen",
        };
        HiddenComputerColumns = (HiddenComputerColumns ?? new())
            .Where(column => !string.IsNullOrWhiteSpace(column) &&
                             !column.Trim().Equals("Select", StringComparison.OrdinalIgnoreCase))
            .Select(column => legacyColumnNames.GetValueOrDefault(column.Trim(), column.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        HiddenProcessColumns = (HiddenProcessColumns ?? new())
            .Where(column => !string.IsNullOrWhiteSpace(column))
            .Select(column => column.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ExamModeDomains ??= new();
        ExamModeApps ??= new();
        ExamModeComputers ??= new();
        ExamModePendingClear ??= new();
        BlockedAppCatalog = (BlockedAppCatalog ?? new())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        DefaultWebsiteBlocklist = (DefaultWebsiteBlocklist ?? new())
            .Select(NormalizeDomain)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SavedWebsites = NormalizeStrings(SavedWebsites, 50);
        RecentWebsites = NormalizeStrings(RecentWebsites, 10);
        OpenWebsiteBrowser = BrowserCatalog.Find(OpenWebsiteBrowser ?? "")?.Id ?? "";
        SavedPrograms = NormalizePrograms(SavedPrograms, 50);
        RecentPrograms = NormalizePrograms(RecentPrograms, 10);
        WindowFilters = (WindowFilters ?? new())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .ToDictionary(pair => pair.Key.Trim(), pair => new WindowFilterSettings
            {
                Text = pair.Value.Text ?? "",
                Selections = (pair.Value.Selections ?? new())
                    .Where(selection => !string.IsNullOrWhiteSpace(selection.Key))
                    .ToDictionary(selection => selection.Key.Trim(), selection => selection.Value ?? "",
                        StringComparer.OrdinalIgnoreCase),
                HiddenColumns = (pair.Value.HiddenColumns ?? new())
                    .Where(column => !string.IsNullOrWhiteSpace(column))
                    .Select(column => column.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            }, StringComparer.OrdinalIgnoreCase);
        DraftWebsites = WebsiteDrafts.Normalize(DraftWebsites);
        QuickActions = QuickActionCatalog.Normalize(QuickActions);
        if (QuickActionDefaultsVersion == 0 && QuickActions.Count == 1 &&
            QuickActions[0].Equals("lock", StringComparison.OrdinalIgnoreCase))
            QuickActions.Add("unlock");
        if (QuickActionDefaultsVersion < 2 && !QuickActions.Contains("send-files", StringComparer.OrdinalIgnoreCase))
            QuickActions.Add("send-files");
        QuickActionDefaultsVersion = 2;
        Schedules ??= new();
        if (ScheduleDefaultsVersion < 1 && !Schedules.Any(schedule => schedule.Action == "timesync"))
            Schedules.Add(ScheduleRuns.SampleTimeSync());
        // Version 2: the sample catches up computers that were off at 08:15 (it was added without it the day before).
        if (ScheduleDefaultsVersion < 2)
            foreach (var sample in Schedules.Where(schedule => schedule.Action == "timesync" && schedule.AllComputers &&
                                                               schedule.Name == ScheduleRuns.SampleTimeSync().Name))
                sample.CatchUp = true;
        // Version 3: a weekly update check and a monthly backup of student work, both switched off.
        if (ScheduleDefaultsVersion < 3)
        {
            if (!Schedules.Any(schedule => schedule.Action == "updatescan")) Schedules.Add(ScheduleRuns.SampleUpdateCheck());
            if (!Schedules.Any(schedule => schedule.Action == "backup")) Schedules.Add(ScheduleRuns.SampleBackup());
        }
        ScheduleDefaultsVersion = Math.Max(ScheduleDefaultsVersion, 3);
        if (string.IsNullOrWhiteSpace(QuickActionMessage)) QuickActionMessage = "Please look at the teacher.";
        FavoriteCommands = FavoriteCommandCatalog.Normalize(FavoriteCommands);
        HostMode = HostMode.Equals(HostModes.Admin, StringComparison.OrdinalIgnoreCase) ? HostModes.Admin : HostModes.Teacher;
        ActivityLogKeepDays = ActivityLogFile.NormalizeKeepDays(ActivityLogKeepDays);
        TeacherVisibleCommands = TeacherCommandCatalog.Normalize(TeacherVisibleCommands);
        foreach (var (version, id) in TeacherCommandCatalog.AddedDefaults.Where(added => added.Version > TeacherDefaultsVersion))
            if (!TeacherVisibleCommands.Contains(id, StringComparer.OrdinalIgnoreCase)) TeacherVisibleCommands.Add(id);
        TeacherDefaultsVersion = Math.Max(TeacherDefaultsVersion, TeacherCommandCatalog.AddedDefaults.Max(added => added.Version));
        WindowsUpdateSnapshots = (WindowsUpdateSnapshots ?? new())
            .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.Address))
            .GroupBy(snapshot => snapshot.Address, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .TakeLast(200)
            .ToList();
        FileTransferHistory = (FileTransferHistory ?? new())
            .Where(record => record is not null && !string.IsNullOrWhiteSpace(record.Id) &&
                             !string.IsNullOrWhiteSpace(record.Address) && !string.IsNullOrWhiteSpace(record.FileName))
            .OrderBy(record => record.CreatedUtc, StringComparer.Ordinal)
            .TakeLast(1000)
            .ToList();
    }

    static List<string> NormalizeStrings(IEnumerable<string>? values, int limit) => (values ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(limit)
        .ToList();

    static string NormalizeDomain(string value)
    {
        value = (value ?? "").Trim().TrimStart('.').ToLowerInvariant();
        return value.StartsWith("www.", StringComparison.Ordinal) ? value[4..] : value;
    }

    static List<SavedProgramLaunch> NormalizePrograms(IEnumerable<SavedProgramLaunch>? values, int limit) => (values ?? [])
        .Where(value => value is not null && !string.IsNullOrWhiteSpace(value.Program))
        .Select(value => new SavedProgramLaunch { Program = value.Program.Trim(), Arguments = value.Arguments.Trim() })
        .DistinctBy(value => $"{value.Program}\0{value.Arguments}", StringComparer.OrdinalIgnoreCase)
        .Take(limit)
        .ToList();

    public void RememberWebsite(string url, bool saveForLater)
    {
        SavedWebsites.RemoveAll(value => value.Equals(url, StringComparison.OrdinalIgnoreCase));
        RecentWebsites.RemoveAll(value => value.Equals(url, StringComparison.OrdinalIgnoreCase));
        (saveForLater ? SavedWebsites : RecentWebsites).Insert(0, url);
        SavedWebsites = NormalizeStrings(SavedWebsites, 50);
        RecentWebsites = NormalizeStrings(RecentWebsites, 10);
        RecentCollectFolders = NormalizeStrings(RecentCollectFolders, 8);
    }

    public void RememberProgram(string program, string arguments, bool saveForLater)
    {
        static bool Matches(SavedProgramLaunch value, string p, string a) =>
            value.Program.Equals(p, StringComparison.OrdinalIgnoreCase) &&
            value.Arguments.Equals(a, StringComparison.OrdinalIgnoreCase);
        SavedPrograms.RemoveAll(value => Matches(value, program, arguments));
        RecentPrograms.RemoveAll(value => Matches(value, program, arguments));
        (saveForLater ? SavedPrograms : RecentPrograms).Insert(0,
            new SavedProgramLaunch { Program = program, Arguments = arguments });
        SavedPrograms = NormalizePrograms(SavedPrograms, 50);
        RecentPrograms = NormalizePrograms(RecentPrograms, 10);
    }

    public void RememberCollectFolder(string folder)
    {
        RecentCollectFolders.RemoveAll(value => value.Equals(folder, StringComparison.OrdinalIgnoreCase));
        RecentCollectFolders.Insert(0, folder);
        RecentCollectFolders = NormalizeStrings(RecentCollectFolders, 8);
    }

    public WindowFilterSettings FilterState(string window)
    {
        if (WindowFilters.TryGetValue(window, out var state)) return state;
        state = new WindowFilterSettings();
        WindowFilters[window] = state;
        return state;
    }

    /// <summary>
    /// Set once the files on disk have been replaced by an import or a restored backup and the Host
    /// is about to restart: the settings still in memory are the old ones and must not be written back.
    /// </summary>
    public static bool SavesSuspended { get; set; }

    // When each settings file was last read or written by this program: a later change is somebody else's.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> KnownWrite = new(StringComparer.OrdinalIgnoreCase);
    [ThreadStatic] static bool _adopting;

    static void Remember(string path)
    {
        try { KnownWrite[path] = File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// If another program changed the saved settings since this one last read or wrote them (the
    /// command line turning Exam mode on or off), takes over its exam list. Returns true when it did.
    /// Everything else stays as this program has it.
    /// </summary>
    public bool AdoptExamChangeMadeElsewhere(string? path = null)
    {
        path ??= PathOnDisk;
        if (_adopting || SavesSuspended) return false;
        try
        {
            if (!File.Exists(path) || !KnownWrite.TryGetValue(path, out var known) || File.GetLastWriteTimeUtc(path) <= known) return false;
            _adopting = true;
            return ExamModeScope.Adopt(this, Load(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
        finally { _adopting = false; }
    }

    public void Save(string? path = null)
    {
        if (SavesSuspended) return;
        path ??= PathOnDisk;
        path = System.IO.Path.GetFullPath(path);
        using var storageLease = HostSettingsCommit.Enter(path);
        // Before writing over them: an exam the command line started or ended must not be lost.
        // Settings this program did not read, or that another program changed since: written over
        // blindly, they are what a teacher finds gone. Keep a copy first and note who wrote.
        var foreign = File.Exists(path) && (!KnownWrite.TryGetValue(path, out var known) || File.GetLastWriteTimeUtc(path) > known);
        var computersBefore = foreign ? HostBackups.Summary(path).Computers : 0;
        AdoptExamChangeMadeElsewhere(path);
        NormalizeHostKeys();
        // Nobody empties a Host that has computers on purpose; if something is about to, keep what is there.
        var emptying = HostBackups.WouldEmpty(path, this);
        if (emptying) HostBackups.Snapshot(path, HostBackups.BeforeEmptying);
        else if (foreign)
            try { HostBackups.Snapshot(path, HostBackups.BeforeOverwrite); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Prepare both payloads before changing either primary file.
        var json = JsonSerializer.Serialize(this, Protocol.Json);
        var protectedKeys = HostSecretStore.Protect(HostSecretStore.From(this));
        try { HostSettingsCommit.Write(path, json, protectedKeys); }
        catch (HostSettingsCommitException ex) when (ex.Outcome == HostSettingsCommitOutcome.Committed)
        {
            Remember(path);
            throw;
        }
        Remember(path);
        if (emptying || foreign)
            HostBackups.Journal(path, emptying ? "EMPTIED (copy kept in backups)"
                : $"wrote over settings it had not read or another program had changed ({computersBefore} computer(s) before, copy kept in backups)");
    }
}
