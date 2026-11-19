// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;
using Microsoft.Win32;

namespace ClassroomManager.Client;

/// <summary>
/// Browser management (see <see cref="BrowserRules"/>). The policies are machine-wide and are
/// written by the service; the browser data belongs to the signed-in user and is handled by that
/// user's helper, which first closes the browsers because their files are in use while they run.
/// </summary>
sealed class BrowserHandler
{
    static readonly string[] BrowserProcesses = ["chrome", "msedge", "firefox"];

    static string StatePath => Path.Combine(ClientConfig.DataDir, "browser-policies.json");

    // ---- policies (service) -------------------------------------------------------------------------

    static BrowserPolicyStore Policies(RegistryKey machine) => new(machine, StatePath);

    /// <summary>
    /// The policies of one user (protocol 1.13, forUser): Chrome, Edge and Firefox also read them from the user's
    /// own registry, where only administrators may write, so the service writes them into the hive Windows
    /// loaded for the signed-in user. Their earlier values are remembered per user, apart from the computer's.
    /// </summary>
    static (BrowserPolicyStore Store, IDisposable Root) Store(string? userSid)
    {
        if (userSid is null)
        {
            var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return (Policies(machine), machine);
        }
        var user = Registry.Users.OpenSubKey(userSid, writable: true)
                   ?? throw new InvalidOperationException("The signed-in user's settings are not loaded.");
        return (new BrowserPolicyStore(user, Path.Combine(ClientConfig.DataDir, $"browser-policies-{userSid}.json")), user);
    }

    public Response PolicyGet(string? userSid = null)
    {
        try
        {
            var (store, root) = Store(userSid);
            using var _ = root;
            var data = new JsonObject();
            foreach (var policy in BrowserRules.Policies) data[policy] = store.State(policy);
            return Response.Success(data);
        }
        catch (Exception ex) { return Response.Fail("Could not read the browser policies: " + ex.Message); }
    }

    public Response PolicySet(JsonObject a, string? userSid = null)
    {
        if (AgentContext.IsUserAgent) return Response.Fail("Browser policies must be changed at the system level, also those of one user.");
        try
        {
            var (store, root) = Store(userSid);
            using var _ = root;
            foreach (var policy in BrowserRules.Policies)
            {
                if (a.Bool(policy) is not { } on) continue;   // not mentioned: left as it is
                store.Set(policy, on);
                Log.Write($"browser.policy.set{(userSid is null ? "" : " (signed-in user)")}: {BrowserRules.PolicyName(policy)} -> {(on ? "on" : "restored")}.");
            }
            return PolicyGet(userSid);
        }
        catch (Exception ex) { return Response.Fail("Could not change the browser policies: " + ex.Message); }
    }

    // ---- installed browsers and the default one (see BrowserCatalog) --------------------------------

    const string StartMenuInternet = @"SOFTWARE\Clients\StartMenuInternet";

    static string AssociationsPath => Path.Combine(ClientConfig.DataDir, "DefaultApps", "default-browser.xml");

    static BrowserRules.Setting DefaultBrowserPolicy => new("Windows", BrowserCatalog.PolicyKey, BrowserCatalog.DefaultPolicyValue, Text: AssociationsPath);

    /// <summary>
    /// The browsers of this computer. In the user's helper it also sees browsers installed only for that
    /// user and which browser opens their web links; in the service, only browsers installed for everyone.
    /// </summary>
    public Response List()
    {
        try
        {
            var installed = Scan(includeUser: AgentContext.IsUserAgent);
            var data = new JsonObject
            {
                [BrowserCatalog.Browsers] = new JsonArray(installed.Select(browser => (JsonNode)browser.ToJson()).ToArray()),
                [BrowserCatalog.Managed] = ManagedDefault()?.Id ?? "",
            };
            if (AgentContext.IsUserAgent)
            {
                var progId = UserLinkProgId();
                data[BrowserCatalog.DefaultProgId] = progId;
                data[BrowserCatalog.Default] = installed.FirstOrDefault(browser => browser.LinkProgId.Equals(progId, StringComparison.OrdinalIgnoreCase))?.Id
                    ?? BrowserCatalog.ByProgId(progId)?.Id ?? "";
            }
            return Response.Success(data);
        }
        catch (Exception ex) { return Response.Fail("Could not list the browsers: " + ex.Message); }
    }

    /// <summary>
    /// Before the client is removed: puts back every browser policy and the default browser it set, for the
    /// computer and for each user whose settings Windows has loaded. Only values it wrote are touched.
    /// </summary>
    public static void RestoreAll()
    {
        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            var store = Policies(machine);
            foreach (var policy in BrowserRules.Policies) store.Set(policy, on: false);
            store.Set([DefaultBrowserPolicy], on: false);
        }
        if (File.Exists(AssociationsPath)) File.Delete(AssociationsPath);
        if (!Directory.Exists(ClientConfig.DataDir)) return;
        foreach (var file in Directory.EnumerateFiles(ClientConfig.DataDir, "browser-policies-S-*.json"))
        {
            var sid = Path.GetFileNameWithoutExtension(file)["browser-policies-".Length..];
            using var user = Registry.Users.OpenSubKey(sid, writable: true);
            if (user is null) continue;   // not signed in: Windows has not loaded that user's settings
            var store = new BrowserPolicyStore(user, file);
            foreach (var policy in BrowserRules.Policies) store.Set(policy, on: false);
        }
    }

    /// <summary>Makes a browser open web links for every user from their next sign-in, or with "" stops doing so.</summary>
    public Response DefaultSet(JsonObject a)
    {
        if (AgentContext.IsUserAgent) return Response.Fail("The default browser is set for every user and must be changed at the system level.");
        try
        {
            var id = a.Str(BrowserCatalog.Browser, "").Trim();
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            var store = Policies(machine);
            if (id.Length == 0)
            {
                store.Set([DefaultBrowserPolicy], on: false);
                if (File.Exists(AssociationsPath)) File.Delete(AssociationsPath);
                Log.Write("browser.default.set: the default browser is no longer set at sign-in");
                return List();
            }

            var browser = Scan(includeUser: false).FirstOrDefault(found => found.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var name = BrowserCatalog.Find(id)?.Name ?? id;
            if (browser is null) return Response.Fail($"{name} is not installed for all users of this computer.");
            if (browser.LinkProgId.Length == 0)
                return Response.Fail($"{name} is installed but has not registered itself with Windows for web links. Start it once, or reinstall it for all users.");

            SignInImageHandler.ReadableByUsers(Path.GetDirectoryName(AssociationsPath)!);
            File.WriteAllText(AssociationsPath, BrowserCatalog.AssociationsXml(browser), new System.Text.UTF8Encoding(false));
            store.Set([DefaultBrowserPolicy], on: true);
            Log.Write($"browser.default.set: {browser.Name} opens web links for every user from their next sign-in");
            return List();
        }
        catch (Exception ex) { return Response.Fail("Could not set the default browser: " + ex.Message); }
    }

    /// <summary>The browsers on this computer, for the signed-in user's session when includeUser is set.</summary>
    internal static List<BrowserCatalog.Installed> FindInstalled(bool includeUser) => Scan(includeUser);

    static List<BrowserCatalog.Installed> Scan(bool includeUser)
    {
        var found = new Dictionary<string, BrowserCatalog.Installed>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in RegistryViews.All)
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            ScanRegistered(machine, found);
        }
        if (includeUser) ScanRegistered(Registry.CurrentUser, found);

        // A main browser that is installed but never registered itself still counts as present.
        foreach (var known in BrowserCatalog.Main.Where(known => !found.ContainsKey(known.Id)))
        {
            var path = KnownPaths(known, includeUser).FirstOrDefault(File.Exists);
            if (path is not null) found[known.Id] = new(known.Id, known.Name, path, Version(path), known.LinkProgId, "", false);
        }
        return found.Values.OrderBy(browser => BrowserCatalog.Find(browser.Id) is { } known ? BrowserCatalog.All.ToList().IndexOf(known) : 99)
            .ThenBy(browser => browser.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void ScanRegistered(RegistryKey root, Dictionary<string, BrowserCatalog.Installed> found)
    {
        using var clients = root.OpenSubKey(StartMenuInternet);
        if (clients is null) return;
        foreach (var name in clients.GetSubKeyNames())
        {
            using var entry = clients.OpenSubKey(name);
            using var command = entry?.OpenSubKey(@"shell\open\command");
            var path = ExePath(command?.GetValue("") as string ?? "");
            // Internet Explorer only opens Edge now; a registration whose program is gone is left over from an uninstall.
            if (path.Length == 0 || !File.Exists(path) || Path.GetFileName(path).Equals("iexplore.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var known = BrowserCatalog.ByExe(path);
            var id = known?.Id ?? name.ToLowerInvariant();
            if (found.ContainsKey(id)) continue;
            using var links = entry!.OpenSubKey(@"Capabilities\URLAssociations");
            using var files = entry.OpenSubKey(@"Capabilities\FileAssociations");
            var linkProgId = links?.GetValue("https") as string ?? links?.GetValue("http") as string ?? known?.LinkProgId ?? "";
            var fileProgId = files?.GetValue(".html") as string ?? files?.GetValue(".htm") as string ?? "";
            found[id] = new(id, known?.Name ?? entry.GetValue("") as string ?? name, path, Version(path), linkProgId, fileProgId, true);
        }
    }

    static IEnumerable<string> KnownPaths(BrowserCatalog.Known known, bool includeUser)
    {
        var programs = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(folder => folder.Length > 0).Distinct().ToList();
        var relative = known.Id switch
        {
            "chrome" => @"Google\Chrome\Application\chrome.exe",
            "edge" => @"Microsoft\Edge\Application\msedge.exe",
            "firefox" => @"Mozilla Firefox\firefox.exe",
            _ => "",
        };
        if (relative.Length == 0) yield break;
        foreach (var folder in programs) yield return Path.Combine(folder, relative);
        if (includeUser && known.Id == "chrome")
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), relative);
    }

    /// <summary>The program of a command line such as "C:\Program Files\...\chrome.exe" --flag.</summary>
    static string ExePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"')) return command.IndexOf('"', 1) is > 1 and var end ? command[1..end] : "";
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe < 0 ? command : command[..(exe + 4)];
    }

    static string Version(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return (info.ProductVersion ?? info.FileVersion ?? "").Trim();
        }
        catch (FileNotFoundException) { return ""; }
    }

    /// <summary>The registration that opens https links for this user; Windows 11 keeps a newer copy under UserChoiceLatest.</summary>
    static string UserLinkProgId()
    {
        foreach (var scheme in new[] { "https", "http" })
            foreach (var choice in new[] { "UserChoiceLatest", "UserChoice" })
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme}\{choice}");
                if (key?.GetValue("ProgId") is string { Length: > 0 } progId) return progId;
            }
        return "";
    }

    /// <summary>The browser a default associations policy makes the default at sign-in, whoever set it.</summary>
    static BrowserCatalog.Known? ManagedDefault()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var policy = machine.OpenSubKey(BrowserCatalog.PolicyKey);
        if (policy?.GetValue(BrowserCatalog.DefaultPolicyValue) is not string { Length: > 0 } path) return null;
        try { return File.Exists(path) ? BrowserCatalog.FromAssociationsXml(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- the signed-in user's browser data (helper) ---------------------------------------------------

    static BrowserDataStore Data() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    public Response ProfilesList()
    {
        try
        {
            return Response.Success(new JsonObject
            {
                [BrowserRules.Profiles] = new JsonArray(Data().Profiles().Select(profile => (JsonNode)profile.ToJson()).ToArray()),
            });
        }
        catch (Exception ex) { return Response.Fail("Could not list the browser profiles: " + ex.Message); }
    }

    public async Task<Response> DataClearAsync(JsonObject a, CancellationToken ct)
    {
        try
        {
            bool history = a.Bool(BrowserRules.History) ?? true, cookies = a.Bool(BrowserRules.Cookies) ?? false, cache = a.Bool(BrowserRules.Cache) ?? true;
            int closed = await CloseBrowsersAsync(ct);
            var cleared = Data().Clear(history, cookies, cache);
            var data = new JsonObject
            {
                [BrowserRules.Files] = cleared.Files, [BrowserRules.Bytes] = cleared.Bytes, [BrowserRules.Failed] = cleared.Failed,
                [BrowserRules.Closed] = closed,
            };
            Log.Write($"browser.data.clear: {closed} browser(s) closed, {BrowserRules.DescribeCleared(data)}.");
            return Response.Success(data);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Response.Fail("Could not clear the browsing data: " + ex.Message); }
    }

    public async Task<Response> ProfilesRemoveAsync(JsonObject a, CancellationToken ct)
    {
        try
        {
            // Named profiles only when the request lists some (an empty list names none); otherwise every extra one.
            IReadOnlySet<string>? only = a[BrowserRules.ProfileKeys] is JsonArray keys
                ? keys.Select(key => key?.GetValue<string>() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            var store = Data();
            if (store.Profiles().All(profile => profile.IsFirst || (only is not null && !only.Contains(profile.Key))))
                return Response.Success(new JsonObject { [BrowserRules.Removed] = new JsonArray(), [BrowserRules.Closed] = 0 });
            int closed = await CloseBrowsersAsync(ct);
            var removed = store.RemoveExtraProfiles(only);
            var data = new JsonObject
            {
                [BrowserRules.Removed] = new JsonArray(removed.Select(profile => (JsonNode)profile.ToJson()).ToArray()),
                [BrowserRules.Closed] = closed,
            };
            Log.Write($"browser.profiles.remove: {closed} browser(s) closed, {BrowserRules.DescribeRemoved(data)}.");
            return Response.Success(data);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Response.Fail("Could not remove the browser profiles: " + ex.Message); }
    }

    /// <summary>
    /// Asks every browser window of this session to close, gives the browsers a few seconds to save
    /// what they keep, then ends what is still running. Returns how many browsers were running.
    /// </summary>
    static async Task<int> CloseBrowsersAsync(CancellationToken ct)
    {
        int session = Process.GetCurrentProcess().SessionId;
        List<Process> Running() => BrowserProcesses.SelectMany(Process.GetProcessesByName)
            .Where(process => { try { return process.SessionId == session; } catch (InvalidOperationException) { return false; } }).ToList();

        var running = Running();
        int browsers = running.Select(process => process.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        foreach (var process in running)
        {
            try { process.CloseMainWindow(); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        if (browsers == 0) return 0;

        for (int waited = 0; waited < 5000 && Running() is { Count: > 0 } left; waited += 250)
        {
            left.ForEach(process => process.Dispose());
            await Task.Delay(250, ct);
        }
        foreach (var process in Running())
        {
            try { process.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
        await Task.Delay(500, ct);   // the files are released a moment after the processes are gone
        return browsers;
    }
}
