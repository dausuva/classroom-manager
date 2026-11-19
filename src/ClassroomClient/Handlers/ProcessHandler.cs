// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Lists processes and controls visible windows in the service or signed-in-user session selected by the Host.</summary>
sealed class ProcessHandler
{
    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    static extern IntPtr GetLastActivePopup(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("wtsapi32.dll", SetLastError = true, EntryPoint = "WTSEnumerateProcessesExW")]
    static extern bool WTSEnumerateProcessesEx(IntPtr server, ref int level, int sessionId, out IntPtr processes, out int count);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSFreeMemoryExW")]
    static extern bool WTSFreeMemoryEx(int type, IntPtr memory, int count);

    /// <summary>WTS_PROCESS_INFO_EX; only the process id and owner SID are read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct WtsProcessInfoEx
    {
        public int SessionId;
        public int ProcessId;
        public IntPtr ProcessName;
        public IntPtr UserSid;
        public int NumberOfThreads;
        public int HandleCount;
        public int PagefileUsage;
        public int PeakPagefileUsage;
        public int WorkingSetSize;
        public int PeakWorkingSetSize;
        public long UserTime;
        public long KernelTime;
    }

    const int WtsAnySession = -2;          // WTS_ANY_SESSION
    const int WtsProcessInfoLevel1 = 1;    // level 1 = WTS_PROCESS_INFO_EX; also WTSTypeProcessInfoLevel1

    const uint GaRootOwner = 3;
    const int GwlExStyle = -20;
    const int WsExToolWindow = 0x00000080;
    const int DwmwaCloaked = 14;
    const uint WmClose = 0x0010;

    public Response List()
    {
        var arr = new JsonArray();
        var windowTitles = VisibleWindowTitles();
        var owners = ProcessOwners();
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                string path = "";
                long memoryBytes = 0;
                long startUtcTicks = 0;
                try { path = proc.MainModule?.FileName ?? ""; } catch { /* protected/system process */ }
                try { memoryBytes = proc.WorkingSet64; } catch { /* exited between enumeration and read */ }
                try { startUtcTicks = proc.StartTime.ToUniversalTime().Ticks; } catch { /* protected/exited process */ }
                var sessionId = SafeSessionId(proc);
                var owner = owners.GetValueOrDefault(proc.Id, "");
                var titles = windowTitles.GetValueOrDefault(proc.Id) ?? [];
                var role = sessionId == 0 || IsSystemOwner(owner) ? "System" : owner.Length > 0 ? "User" : "Unknown";
                var kind = titles.Count > 0 ? $"{role} / windowed app" : $"{role} / background";

                arr.Add(new JsonObject
                {
                    ["pid"] = proc.Id,
                    ["name"] = proc.ProcessName,
                    ["sessionId"] = sessionId,
                    ["title"] = string.Join(" | ", titles),
                    ["path"] = path,
                    ["version"] = ProcessVersion(path, versions),
                    ["description"] = ProcessDescription(path, descriptions),
                    ["startUtcTicks"] = startUtcTicks,
                    ["memoryMb"] = Math.Round(memoryBytes / 1024.0 / 1024.0, 1),
                    ["owner"] = owner,
                    ["role"] = role,
                    ["kind"] = kind,
                });
            }
        }
        return Response.Success(new JsonObject { ["machineName"] = Environment.MachineName, ["processes"] = arr });
    }

    public Response Windows()
    {
        var windows = new JsonArray();
        var appIds = new HashSet<int>();
        foreach (var (pid, titles) in VisibleWindowTitles())
        {
            string processName;
            try
            {
                using var process = Process.GetProcessById(pid);
                processName = process.ProcessName;
            }
            catch { processName = ""; }
            appIds.Add(pid);
            foreach (var title in titles)
                windows.Add(new JsonObject { ["pid"] = pid, ["process"] = processName, ["title"] = title });
        }
        return Response.Success(new JsonObject
        {
            ["appCount"] = appIds.Count,
            ["windowCount"] = windows.Count,
            ["windows"] = windows,
        });
    }

    /// <summary>
    /// The website each open browser window shows (browser.sites). Only the domain leaves this
    /// computer, with the window title the teacher can already see through proc.windows; the full
    /// address, which can hold search words and account names, is not sent.
    /// </summary>
    public Response Sites()
    {
        var sites = new JsonArray();
        foreach (var window in BrowserAddressReader.Read())
            sites.Add(new JsonObject
            {
                [BrowserSites.Browser] = window.Browser,
                [BrowserSites.Title] = window.Title,
                [BrowserSites.Domain] = BrowserSites.DomainOf(window.Address),
            });
        return Response.Success(new JsonObject { [BrowserSites.Sites] = sites });
    }

    public Response CloseApps()
    {
        var windows = new List<(IntPtr Window, int ProcessId)>();
        EnumWindows((window, _) =>
        {
            if (!IsAltTabWindow(window)) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId > 0) windows.Add((window, (int)processId));
            return true;
        }, IntPtr.Zero);

        var sent = 0;
        foreach (var item in windows)
            if (PostMessage(item.Window, WmClose, UIntPtr.Zero, IntPtr.Zero)) sent++;
        var apps = windows.Select(item => item.ProcessId).Distinct().Count();
        Log.Write($"proc.close-apps: sent WM_CLOSE to {sent} window(s) across {apps} app(s).");
        return Response.Success(new JsonObject
        {
            ["appCount"] = apps,
            ["windowCount"] = windows.Count,
            ["closeRequests"] = sent,
        });
    }

    static Dictionary<int, List<string>> VisibleWindowTitles()
    {
        var result = new Dictionary<int, List<string>>();
        EnumWindows((window, _) =>
        {
            if (!IsAltTabWindow(window)) return true;
            var length = GetWindowTextLength(window);
            if (length <= 0) return true;
            var text = new StringBuilder(length + 1);
            if (GetWindowText(window, text, text.Capacity) <= 0) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0) return true;
            if (!result.TryGetValue((int)processId, out var titles)) result[(int)processId] = titles = new List<string>();
            var title = text.ToString().Trim();
            if (title.Length > 0 && !titles.Contains(title, StringComparer.Ordinal)) titles.Add(title);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    static bool IsAltTabWindow(IntPtr window)
    {
        if (!IsWindowVisible(window) || GetWindowTextLength(window) <= 0) return false;
        if ((GetWindowLong(window, GwlExStyle) & WsExToolWindow) != 0) return false;
        if (DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return false;

        var root = GetAncestor(window, GaRootOwner);
        var candidate = root;
        while (true)
        {
            var popup = GetLastActivePopup(candidate);
            if (popup == candidate) break;
            candidate = popup;
            if (IsWindowVisible(candidate)) break;
        }
        return candidate == window;
    }

    /// <summary>
    /// "DOMAIN\user" for every process whose owner can be read, from one WTSEnumerateProcessesEx call.
    /// Replaces a WMI GetOwner call per process (T-158): that kept WMI loaded in the service (about 2.6 MB
    /// and a thread) and lost every remaining owner when one process ended during the listing. A process
    /// for which Windows gives no owner SID is left without an owner, as when WMI's GetOwner failed.
    /// </summary>
    internal static Dictionary<int, string> ProcessOwners()
    {
        var level = WtsProcessInfoLevel1;
        if (!WTSEnumerateProcessesEx(IntPtr.Zero, ref level, WtsAnySession, out var buffer, out var count))
        {
            Log.Write($"proc.list: process owners unavailable: error {Marshal.GetLastWin32Error()}.");
            return new();
        }
        var entries = new List<(int ProcessId, SecurityIdentifier? Owner)>(count);
        try
        {
            var size = Marshal.SizeOf<WtsProcessInfoEx>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsProcessInfoEx>(buffer + i * size);
                SecurityIdentifier? owner = null;
                if (info.UserSid != IntPtr.Zero)
                {
                    try { owner = new SecurityIdentifier(info.UserSid); }
                    catch (ArgumentException) { }
                }
                entries.Add((info.ProcessId, owner));
            }
        }
        finally { WTSFreeMemoryEx(WtsProcessInfoLevel1, buffer, count); }
        return MapOwners(entries, AccountName);
    }

    /// <summary>
    /// Owner names by process id. Each SID is resolved once per listing; a process without an owner, or
    /// one whose SID does not resolve to an account, is left out and does not affect the others.
    /// </summary>
    internal static Dictionary<int, string> MapOwners(IEnumerable<(int ProcessId, SecurityIdentifier? Owner)> entries,
        Func<SecurityIdentifier, string?> resolve)
    {
        var result = new Dictionary<int, string>();
        var names = new Dictionary<SecurityIdentifier, string?>();
        foreach (var (processId, owner) in entries)
        {
            if (owner is null) continue;
            if (!names.TryGetValue(owner, out var name))
            {
                try { name = resolve(owner); }
                catch (SystemException) { name = null; }   // IdentityNotMappedException and lookup failures
                names[owner] = name;
            }
            if (!string.IsNullOrEmpty(name)) result[processId] = name;
        }
        return result;
    }

    static string? AccountName(SecurityIdentifier sid) => ((NTAccount)sid.Translate(typeof(NTAccount))).Value;

    static bool IsSystemOwner(string owner) =>
        owner.StartsWith("NT AUTHORITY\\", StringComparison.OrdinalIgnoreCase) ||
        owner.StartsWith("NT SERVICE\\", StringComparison.OrdinalIgnoreCase) ||
        owner.EndsWith("\\SYSTEM", StringComparison.OrdinalIgnoreCase);

    static int SafeSessionId(Process proc)
    {
        try { return proc.SessionId; }
        catch { return -1; }
    }

    public Response Kill(JsonObject a)
    {
        int pid = a.Int("pid") ?? -1;
        if (pid <= 0) return Response.Fail("Missing or invalid 'pid'.");

        try
        {
            using var proc = Process.GetProcessById(pid);
            var name = proc.ProcessName;
            var expectedName = a.Str("expectedName", "");
            if (expectedName.Length > 0 && !name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                return Response.Fail($"Process id {pid} now belongs to '{name}', not '{expectedName}'. Refresh before ending it.");
            var expectedStartUtcTicks = a.Long("expectedStartUtcTicks");
            if (expectedStartUtcTicks > 0)
            {
                long actualStartUtcTicks;
                try { actualStartUtcTicks = proc.StartTime.ToUniversalTime().Ticks; }
                catch (Exception ex) { return Response.Fail("Could not verify the process start time: " + ex.Message); }
                if (actualStartUtcTicks != expectedStartUtcTicks)
                    return Response.Fail($"Process id {pid} has been reused by another '{name}' process. Refresh before ending it.");
            }
            proc.Kill(entireProcessTree: a.Bool("tree") ?? false);
            Log.Write($"proc.kill: ended {name} (pid {pid}).");
            return Response.Success(new JsonObject { ["name"] = name });
        }
        catch (ArgumentException) { return Response.Fail("No process with that id (it may have already exited)."); }
        catch (Exception ex) { return Response.Fail($"Could not end the process: {ex.Message}"); }
    }

    /// <summary>
    /// The name a person knows a program by ("Microsoft Edge" for msedge), read from its file's version
    /// information once per file. Empty when the file has none or cannot be read.
    /// </summary>
    static string ProcessDescription(string path, Dictionary<string, string> cache)
    {
        if (path.Length == 0) return "";
        if (cache.TryGetValue(path, out var description)) return description;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            description = DescriptionOf(info.FileDescription, info.ProductName, path);
        }
        catch { description = ""; }
        cache[path] = description;
        return description;
    }

    /// <summary>
    /// The file description, or the product name when the description only repeats the file's own name
    /// ("msedge", "svchost.exe"), or nothing. Surrounding spaces and a stray line break are dropped.
    /// </summary>
    internal static string DescriptionOf(string? fileDescription, string? productName, string path)
    {
        var file = Path.GetFileNameWithoutExtension(path);
        bool Useful(string? text) => !string.IsNullOrWhiteSpace(text) &&
            !text.Trim().Equals(file, StringComparison.OrdinalIgnoreCase) &&
            !text.Trim().Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
        var chosen = Useful(fileDescription) ? fileDescription : Useful(productName) ? productName : "";
        return string.Join(' ', (chosen ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    static string ProcessVersion(string path, Dictionary<string, string> cache)
    {
        if (path.Length == 0) return "";
        if (cache.TryGetValue(path, out var version)) return version;
        try { version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? ""; }
        catch { version = ""; }
        cache[path] = version;
        return version;
    }
}
