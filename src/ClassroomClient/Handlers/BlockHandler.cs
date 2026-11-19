// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;
using Microsoft.Win32;

namespace ClassroomManager.Client;

sealed class ExamBlockState
{
    public bool Enabled { get; set; }
    public List<string> Domains { get; set; } = new();
    public List<string> Apps { get; set; } = new();
}

/// <summary>
/// Blocks websites (by rewriting the hosts file) and apps (by ending matching processes on
/// sight). Both are system-level only - deliberately never forwarded to the per-user helper,
/// since neither needs the signed-in student's own token and a hosts-file edit needs admin rights
/// anyway. The app blocklist persists to disk and is re-applied by <see cref="RunWatcherAsync"/>,
/// which the service runs for as long as it's up, so a blocked app that's relaunched (or that was
/// already running before the block was set) gets ended again within a few seconds.
/// </summary>
sealed class BlockHandler
{
    const string MarkerStart = "# >>> ClassroomManager block start - do not edit between these lines >>>";
    const string MarkerEnd = "# <<< ClassroomManager block end <<<";

    static string HostsPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
    static string AppsListPath => Path.Combine(ClientConfig.DataDir, "blocked-apps.json");
    static string DomainsListPath => Path.Combine(ClientConfig.DataDir, "blocked-websites.json");
    static string ExamListPath => Path.Combine(ClientConfig.DataDir, "exam-block.json");

    readonly object _gate = new();
    readonly ExamDeviceHandler _devices = new();
    List<string> _blockedApps;
    List<string> _blockedDomains;
    ExamBlockState _exam;

    public int BlockedWebsiteCount
    {
        get { lock (_gate) return EffectiveDomains().Count; }
    }

    /// <summary>False for the helper's handler, which never reads or writes the block lists.</summary>
    public bool Persisted { get; }

    /// <summary>Every command this handler answers; see <see cref="Cmd"/>.</summary>
    internal static bool Handles(string cmd) => cmd.StartsWith("block.", StringComparison.Ordinal);

    /// <summary>
    /// For the per-user helper (T-160): empty lists, nothing read from or written to
    /// C:\ProgramData\ClassroomManager. The helper may not write there, and before this it tried at every
    /// start; had it been allowed, it would have replaced the service's lists with empty ones.
    /// </summary>
    internal static BlockHandler ForHelper() => new(persisted: false);

    public BlockHandler() : this(persisted: true) { }

    BlockHandler(bool persisted)
    {
        Persisted = persisted;
        if (!persisted)
        {
            _blockedApps = new();
            _blockedDomains = new();
            _exam = new();
            return;
        }
        _blockedApps = LoadList(AppsListPath, "app");
        _blockedDomains = File.Exists(DomainsListPath)
            ? LoadList(DomainsListPath, "website")
            : TryReadManagedDomainsFromHosts();
        _exam = LoadExam();
        _exam.Domains ??= new();
        _exam.Apps ??= new();
        SaveList(DomainsListPath, _blockedDomains, "website");
        // A Client updated while websites are blocked: the block must hold in browsers with secure DNS too.
        if (!AgentContext.IsUserAgent && EffectiveDomains().Count > 0) SetSecureDnsOff(true);
    }

    /// <summary>
    /// Turns the browsers' own secure DNS off for as long as any website is blocked, and puts back
    /// what was set before when nothing is blocked any more (see <see cref="BrowserRules.NoSecureDns"/>).
    /// Chrome and Edge follow at once; Firefox reads the setting when it starts.
    /// </summary>
    /// <summary>"turned off", "partly turned off" or "as the browsers have it", for the Host and Classroom Health.</summary>
    static string SecureDnsState()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return new BrowserPolicyStore(machine, Path.Combine(ClientConfig.DataDir, "secure-dns-policy.json")).State(BrowserRules.NoSecureDns) switch
            {
                "on" => "turned off",
                "partly" => "partly turned off",
                _ => "as the browsers have it",
            };
        }
        catch { return ""; }
    }

    public static void SetSecureDnsOff(bool on)
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            var statePath = Path.Combine(ClientConfig.DataDir, "secure-dns-policy.json");
            var store = new BrowserPolicyStore(machine, statePath);
            // Nothing to do: already off for the browsers, or never turned off by this Client.
            if (on ? store.State(BrowserRules.NoSecureDns) == "on" : !File.Exists(statePath)) return;
            store.Set(BrowserRules.NoSecureDns, on);
            Log.Write(on ? "block.website: browsers' secure DNS turned off while websites are blocked."
                         : "block.website: browsers' secure DNS setting put back.");
        }
        catch (Exception ex) { Log.Write($"block.website: secure DNS setting not changed: {ex.Message}"); }
    }

    static List<string> LoadList(string path, string label)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), Protocol.Json) ?? new();
        }
        catch (Exception ex) { Log.Write($"block.{label}: could not load saved blocklist: {ex.Message}"); }
        return new();
    }

    static void SaveList(string path, List<string> values, string label)
    {
        try
        {
            Directory.CreateDirectory(ClientConfig.DataDir);
            File.WriteAllText(path, JsonSerializer.Serialize(values, Protocol.Json));
        }
        catch (Exception ex) { Log.Write($"block.{label}: could not save blocklist: {ex.Message}"); }
    }

    static ExamBlockState LoadExam()
    {
        try
        {
            if (File.Exists(ExamListPath))
                return JsonSerializer.Deserialize<ExamBlockState>(File.ReadAllText(ExamListPath), Protocol.Json) ?? new();
        }
        catch (Exception ex) { Log.Write($"block.exam: could not load saved overlay: {ex.Message}"); }
        return new ExamBlockState();
    }

    void SaveExam()
    {
        try
        {
            Directory.CreateDirectory(ClientConfig.DataDir);
            File.WriteAllText(ExamListPath, JsonSerializer.Serialize(_exam, Protocol.Json));
        }
        catch (Exception ex) { Log.Write($"block.exam: could not save overlay: {ex.Message}"); }
    }

    // ---- websites (hosts file) ---------------------------------------------------------------

    /// <summary>
    /// Reads back only what this tool itself owns: the domains currently inside the managed block.
    /// Never returns or lets the host touch anything outside it - "otherEntries" just flags that the
    /// hosts file also has unmanaged lines of its own, so a teacher isn't surprised if blocking
    /// everything doesn't seem to fully take effect (something else may already be redirecting a name).
    /// </summary>
    public Response ListHosts()
    {
        try
        {
            var permanent = new JsonArray();
            var exam = new JsonArray();
            var effective = new JsonArray();
            bool examEnabled;
            lock (_gate)
            {
                foreach (var domain in _blockedDomains) permanent.Add(domain);
                examEnabled = _exam.Enabled;
                if (examEnabled)
                    foreach (var domain in _exam.Domains) exam.Add(domain);
                foreach (var domain in EffectiveDomains()) effective.Add(domain);
            }
            return Response.Success(new JsonObject
            {
                ["domains"] = permanent,
                ["examDomains"] = exam,
                ["effectiveDomains"] = effective,
                ["examEnabled"] = examEnabled,
                ["otherEntries"] = CountUnmanagedHostEntries(),
                ["secureDns"] = SecureDnsState(),
            });
        }
        catch (Exception ex) { return Response.Fail($"Could not read the hosts file: {ex.Message}"); }
    }

    public Response SetHosts(JsonObject a)
    {
        var domains = (a["domains"] as JsonArray)?
            .Select(d => d?.GetValue<string>() ?? "")
            .Select(NormalizeDomain)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        try
        {
            lock (_gate)
            {
                _blockedDomains = domains;
                SaveList(DomainsListPath, _blockedDomains, "website");
                ApplyEffectiveHosts();
            }
            Log.Write($"block.hosts.set: {domains.Count} domain(s) blocked.");
            return Response.Success(new JsonObject { ["count"] = domains.Count, ["effectiveCount"] = BlockedWebsiteCount });
        }
        catch (Exception ex) { return Response.Fail($"Could not update the hosts file: {ex.Message}"); }
    }

    /// <summary>
    /// Adds domains to the permanent block and keeps everything already in it. This is the one change
    /// a Teacher key may make to the block, so unlike <see cref="SetHosts"/> it accepts only plain
    /// website names and can never shorten the list.
    /// </summary>
    public Response AddHosts(JsonObject a)
    {
        var asked = (a["domains"] as JsonArray)?.Select(d => d?.GetValue<string>() ?? "").Where(d => d.Trim().Length > 0).ToList() ?? new List<string>();
        var domains = asked.Select(BrowserSites.DomainOf).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (domains.Count == 0)
            return Response.Fail(asked.Count == 0 ? "No website was named." : "None of the names is a website domain such as example.com.");

        try
        {
            int added, total;
            lock (_gate)
            {
                var merged = _blockedDomains.Union(domains, StringComparer.OrdinalIgnoreCase).ToList();
                added = merged.Count - _blockedDomains.Count;
                total = merged.Count;
                if (added > 0)
                {
                    _blockedDomains = merged;
                    SaveList(DomainsListPath, _blockedDomains, "website");
                    ApplyEffectiveHosts();
                }
            }
            Log.Write($"block.hosts.add: {added} domain(s) added ({string.Join(", ", domains)}); {total} blocked.");
            return Response.Success(new JsonObject
            {
                ["added"] = added, ["count"] = total, ["rejected"] = asked.Count - domains.Count, ["effectiveCount"] = BlockedWebsiteCount,
            });
        }
        catch (Exception ex) { return Response.Fail($"Could not update the hosts file: {ex.Message}"); }
    }

    public Response ClearHosts()
    {
        try
        {
            lock (_gate)
            {
                _blockedDomains = new List<string>();
                SaveList(DomainsListPath, _blockedDomains, "website");
                ApplyEffectiveHosts();
            }
            Log.Write("block.hosts.clear: website block removed.");
            return Response.Success(new JsonObject { ["effectiveCount"] = BlockedWebsiteCount });
        }
        catch (Exception ex) { return Response.Fail($"Could not update the hosts file: {ex.Message}"); }
    }

    /// <summary>
    /// Makes every Classroom Manager-owned website entry match one authoritative list. Unlike the
    /// ordinary permanent-list setter, this also disables and clears the entire stale exam overlay.
    /// Hosts-file lines outside our marker remain untouched.
    /// </summary>
    public Response ResetHosts(JsonObject args)
    {
        var domains = (args["domains"] as JsonArray)?
            .Select(node => NormalizeDomain(node?.GetValue<string>() ?? ""))
            .Where(domain => domain.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        try
        {
            int clearedExamDomains;
            int clearedExamApps;
            lock (_gate)
            {
                clearedExamDomains = _exam.Domains.Count;
                clearedExamApps = _exam.Apps.Count;
                _blockedDomains = domains;
                _exam = new ExamBlockState();
                SaveList(DomainsListPath, _blockedDomains, "website");
                SaveExam();
                ApplyEffectiveHosts();
            }
            _devices.Restore();
            Log.Write($"block.hosts.reset: {domains.Count} default domain(s); Exam mode disabled and cleared ({clearedExamDomains} website, {clearedExamApps} app entries).");
            return Response.Success(new JsonObject
            {
                ["count"] = domains.Count,
                ["clearedExamDomains"] = clearedExamDomains,
                ["clearedExamApps"] = clearedExamApps,
                ["examDisabled"] = true,
                ["effectiveCount"] = BlockedWebsiteCount,
            });
        }
        catch (Exception ex) { return Response.Fail("Could not reset the managed website blocklist: " + ex.Message); }
    }

    static List<string> ReadHostsWithoutManagedBlock()
    {
        var result = new List<string>();
        bool inBlock = false;
        foreach (var line in File.Exists(HostsPath) ? File.ReadAllLines(HostsPath) : Array.Empty<string>())
        {
            var trimmed = line.Trim();
            if (trimmed == MarkerStart) { inBlock = true; continue; }
            if (trimmed == MarkerEnd) { inBlock = false; continue; }
            if (!inBlock) result.Add(line);
        }
        // Trim trailing blank lines so repeated set/clear calls don't grow the file forever.
        while (result.Count > 0 && result[^1].Trim().Length == 0) result.RemoveAt(result.Count - 1);
        return result;
    }

    static List<string> ReadManagedDomainsFromHosts()
    {
        var domains = new List<string>();
        var inBlock = false;
        foreach (var line in File.Exists(HostsPath) ? File.ReadAllLines(HostsPath) : Array.Empty<string>())
        {
            var trimmed = line.Trim();
            if (trimmed == MarkerStart) { inBlock = true; continue; }
            if (trimmed == MarkerEnd) { inBlock = false; continue; }
            if (!inBlock || trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var domain = NormalizeDomain(parts[1]);
            if (!domains.Contains(domain, StringComparer.OrdinalIgnoreCase)) domains.Add(domain);
        }
        return domains;
    }

    static List<string> TryReadManagedDomainsFromHosts()
    {
        try { return ReadManagedDomainsFromHosts(); }
        catch (Exception ex)
        {
            Log.Write($"block.website: could not migrate the managed hosts block: {ex.Message}");
            return new List<string>();
        }
    }

    static int CountUnmanagedHostEntries()
    {
        var inBlock = false;
        var count = 0;
        foreach (var line in File.Exists(HostsPath) ? File.ReadAllLines(HostsPath) : Array.Empty<string>())
        {
            var trimmed = line.Trim();
            if (trimmed == MarkerStart) { inBlock = true; continue; }
            if (trimmed == MarkerEnd) { inBlock = false; continue; }
            if (!inBlock && trimmed.Length > 0 && !trimmed.StartsWith('#')) count++;
        }
        return count;
    }

    void ApplyEffectiveHosts()
    {
        var domains = EffectiveDomains();
        var lines = ReadHostsWithoutManagedBlock();
        if (domains.Count > 0)
        {
            lines.Add(MarkerStart);
            foreach (var domain in domains)
            {
                lines.Add($"0.0.0.0 {domain}");
                if (!domain.StartsWith("www.", StringComparison.Ordinal)) lines.Add($"0.0.0.0 www.{domain}");
            }
            lines.Add(MarkerEnd);
        }
        File.WriteAllLines(HostsPath, lines);
        FlushDns();
        SetSecureDnsOff(domains.Count > 0);
    }

    List<string> EffectiveDomains() => _blockedDomains
        .Union(_exam.Enabled ? _exam.Domains : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase)
        .ToList();

    static string NormalizeDomain(string value)
    {
        var domain = value.Trim().TrimStart('.').ToLowerInvariant();
        // One name per hosts line: a space, a line break or a comment sign would let a "domain" write lines of its own.
        if (domain.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '#')) return "";
        return domain.StartsWith("www.", StringComparison.Ordinal) ? domain[4..] : domain;
    }

    static void FlushDns()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ipconfig.exe", "/flushdns")
            { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(5000);
        }
        catch { /* best effort - new lookups still see the hosts file change either way */ }
    }

    // ---- apps (process blocklist enforced by RunWatcherAsync) ---------------------------------

    public Response SetApps(JsonObject a)
    {
        var names = (a["names"] as JsonArray)?
            .Select(n => n?.GetValue<string>() ?? "")
            .Select(NormalizeApp)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        lock (_gate)
        {
            _blockedApps = names;
            SaveList(AppsListPath, _blockedApps, "app");
            KillNow(EffectiveApps());
        }
        Log.Write($"block.apps.set: watching for {names.Count} process name(s): {string.Join(", ", names)}");
        return Response.Success(new JsonObject { ["count"] = names.Count });
    }

    public Response ClearApps()
    {
        lock (_gate)
        {
            _blockedApps = new List<string>();
            SaveList(AppsListPath, _blockedApps, "app");
        }
        Log.Write("block.apps.clear: app block removed.");
        return Response.Success();
    }

    public Response ListApps()
    {
        var apps = new JsonArray();
        lock (_gate)
            foreach (var name in _blockedApps)
                apps.Add(IsPath(name) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe");
        return Response.Success(new JsonObject { ["names"] = apps });
    }

    public Response SetExamMode(JsonObject args)
    {
        var domains = (args["domains"] as JsonArray)?.Select(node => NormalizeDomain(node?.GetValue<string>() ?? ""))
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();
        var apps = (args["apps"] as JsonArray)?.Select(node => NormalizeApp(node?.GetValue<string>() ?? ""))
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();
        try
        {
            lock (_gate)
            {
                _exam = new ExamBlockState { Enabled = true, Domains = domains, Apps = apps };
                SaveExam();
                ApplyEffectiveHosts();
                KillNow(EffectiveApps());
            }
            Log.Write($"block.exam.set: {domains.Count} domain(s), {apps.Count} app(s).");
            return Response.Success(new JsonObject
            {
                ["domains"] = domains.Count,
                ["apps"] = apps.Count,
                ["effectiveWebsiteCount"] = BlockedWebsiteCount,
            });
        }
        catch (Exception ex) { return Response.Fail("Could not enable exam mode: " + ex.Message); }
    }

    public Response ClearExamMode()
    {
        try
        {
            lock (_gate)
            {
                _exam.Enabled = false;
                SaveExam();
                ApplyEffectiveHosts();
            }
            _devices.Restore();
            Log.Write("block.exam.clear: exam overlay disabled; permanent blocks preserved.");
            return Response.Success(new JsonObject { ["effectiveWebsiteCount"] = BlockedWebsiteCount });
        }
        catch (Exception ex) { return Response.Fail("Could not disable exam mode: " + ex.Message); }
    }

    /// <summary>Device controls belong to the exam overlay: set here, removed again by <see cref="ClearExamMode"/>.</summary>
    public Response SetExamDevices(JsonObject args) => _devices.Set(args);

    List<string> EffectiveApps() => _blockedApps
        .Union(_exam.Enabled ? _exam.Apps : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase)
        .ToList();

    static string NormalizeApp(string name)
    {
        name = Environment.ExpandEnvironmentVariables(name.Trim().Trim('"'));
        if (name.Length == 0) return "";
        if (IsPath(name))
        {
            try { return Path.GetFullPath(name); }
            catch { return ""; }
        }
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    static bool IsPath(string value) =>
        value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar);

    /// <summary>Runs only in the SYSTEM service for as long as it's up: periodically ends anything on the current blocklist.</summary>
    public async Task RunWatcherAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            List<string> names;
            lock (_gate) names = EffectiveApps();
            if (names.Count > 0) KillNow(names);
            _devices.Enforce();
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Never ended, whatever a block list says: this client (blocking it would cut the computer off
    /// from the Host until someone repaired it by hand) and the processes Windows cannot run without.
    /// </summary>
    static readonly HashSet<string> NeverEnded = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClassroomClient", "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "fontdrvhost",
    };

    static void KillNow(List<string> names)
    {
        if (names.Count == 0) return;
        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                try
                {
                    if (proc.Id == Environment.ProcessId || NeverEnded.Contains(proc.ProcessName)) continue;
                    if (!MatchesBlockedApp(proc, names)) continue;
                    proc.Kill(entireProcessTree: true);
                    Log.Write($"block.apps: ended {proc.ProcessName} (pid {proc.Id}).");
                }
                catch { /* already exited, protected, or access denied - nothing more to do */ }
            }
        }
    }

    static bool MatchesBlockedApp(Process process, List<string> entries)
    {
        if (entries.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) return true;
        if (!entries.Any(IsPath)) return false;
        string path;
        try { path = process.MainModule?.FileName ?? ""; }
        catch { return false; }
        return path.Length > 0 && entries.Where(IsPath).Contains(path, StringComparer.OrdinalIgnoreCase);
    }
}
