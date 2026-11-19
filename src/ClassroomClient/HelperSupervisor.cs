// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.InteropServices;

namespace ClassroomManager.Client;

/// <summary>
/// Runs only in the SYSTEM service. The per-user helper is normally started once at logon via the
/// HKLM Run key; nothing relaunches it if a student closes it from Task Manager, it crashes, or a
/// new interactive session shows up after the service is already running. This periodically checks
/// every active interactive session against <see cref="UserBridge.ConnectedSessions"/> and launches
/// a fresh helper into any session that's missing one, using the same "run this exe as that
/// session's user" technique Windows itself uses for interactive services
/// (WTSQueryUserToken + CreateProcessAsUser) rather than anything that touches the pipe or the
/// network directly.
///
/// This is Windows session/token interop and is inherently a bit finicky (session 0 isolation,
/// UAC, RDP vs console sessions) - it's written to fail safe (log and move on) rather than crash
/// the service if a call doesn't work on a particular machine, but it genuinely needs testing
/// against a real signed-in student session, not just a successful build.
/// </summary>
sealed class HelperSupervisor
{
    static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(20);

    readonly UserBridge _bridge;
    readonly string _exePath;

    public HelperSupervisor(UserBridge bridge)
    {
        _bridge = bridge;
        _exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ClassroomClient.exe");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!PackagedClient.ServiceStartsHelpers(ClassroomManager.Shared.PackageIdentity.IsPackaged))
        {
            Log.Write("HelperSupervisor: this Client is a Windows package; the package's startup task starts the helper at sign-in.");
            return;
        }
        WtsNative.EnsurePrivileges();

        // Give Windows a head start at boot/logon so the normal Run-key launch wins the race
        // instead of two helpers briefly fighting over the same named pipe.
        try { await Task.Delay(TimeSpan.FromSeconds(25), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { CheckSessions(); }
            catch (Exception ex) { Log.Write($"HelperSupervisor: {ex.Message}"); }
            try { await Task.Delay(CheckInterval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    void CheckSessions()
    {
        var connected = _bridge.ConnectedSessions;
        foreach (var sessionId in WtsNative.EnumerateActiveUserSessions())
        {
            if (connected.Contains(sessionId)) continue;
            if (WtsNative.TryLaunchInSession(sessionId, _exePath, out var error))
                Log.Write($"HelperSupervisor: relaunched the helper in session {sessionId} (no helper was connected).");
            else
                Log.Write($"HelperSupervisor: could not relaunch the helper in session {sessionId}: {error}");
        }
    }
}

/// <summary>P/Invoke needed to enumerate interactive sessions and launch a process as their signed-in user.</summary>
static class WtsNative
{
    enum ConnectState { Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init }

    [StructLayout(LayoutKind.Sequential)]
    struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPStr)] public string pWinStationName;
        public ConnectState State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privileges; }

    const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    const uint TOKEN_QUERY = 0x0008;
    const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const uint CREATE_NEW_CONSOLE = 0x00000010;
    const int SECURITY_IMPERSONATION = 2;   // SecurityImpersonation
    const int TOKEN_PRIMARY = 1;            // TokenPrimary
    const uint MAXIMUM_ALLOWED = 0x02000000;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, string? commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    /// <summary>
    /// WTSQueryUserToken needs SeTcbPrivilege, and CreateProcessAsUser needs SeIncreaseQuotaPrivilege
    /// and SeAssignPrimaryTokenPrivilege. LocalSystem's token holds all three, but Windows keeps
    /// privileges disabled by default until a process explicitly enables them.
    /// </summary>
    public static void EnsurePrivileges()
    {
        EnablePrivilege("SeTcbPrivilege");
        EnablePrivilege("SeIncreaseQuotaPrivilege");
        EnablePrivilege("SeAssignPrimaryTokenPrivilege");
    }

    static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return;
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED } };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }

    public static List<int> EnumerateActiveUserSessions()
    {
        var result = new List<int>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var pInfo, out var count)) return result;
        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(IntPtr.Add(pInfo, i * size));
                if (info.State == ConnectState.Active && info.SessionId != 0)
                    result.Add(info.SessionId);
            }
        }
        finally { WTSFreeMemory(pInfo); }
        return result;
    }

    public static bool TryLaunchInSession(int sessionId, string exePath, out string error)
    {
        error = "";
        IntPtr userToken = IntPtr.Zero, primaryToken = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken))
            {
                error = $"WTSQueryUserToken failed (Win32 error {Marshal.GetLastWin32Error()})";
                return false;
            }
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero, SECURITY_IMPERSONATION, TOKEN_PRIMARY, out primaryToken))
            {
                error = $"DuplicateTokenEx failed (Win32 error {Marshal.GetLastWin32Error()})";
                return false;
            }
            if (!CreateEnvironmentBlock(out env, primaryToken, false))
                env = IntPtr.Zero; // not fatal - CreateProcessAsUser accepts a null environment block

            var startupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = "winsta0\\default" };
            string commandLine = $"\"{exePath}\"";
            bool ok = CreateProcessAsUser(primaryToken, null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE, env, null, ref startupInfo, out var pi);
            if (!ok)
            {
                error = $"CreateProcessAsUser failed (Win32 error {Marshal.GetLastWin32Error()})";
                return false;
            }
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            return true;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }
}
