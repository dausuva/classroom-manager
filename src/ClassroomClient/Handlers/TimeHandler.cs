// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Sets the computer's clock. Runs in the SYSTEM service.
///   mode "host": set the clock to the UTC time the host sent (args.utcTicks)
///   mode "ntp":  ask the Windows Time service to resync (w32tm /resync)
/// Optional args.timezone: a Windows time-zone id (e.g. "FLE Standard Time") to apply with tzutil.
/// </summary>
sealed class TimeHandler
{
    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public long Luid;
        public int Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetSystemTime(ref SYSTEMTIME st);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int length,
        IntPtr previous, IntPtr returnLength);

    public async Task<Response> SyncAsync(JsonObject a, CancellationToken ct)
    {
        var mode = a.Str("mode", "host").ToLowerInvariant();
        var notes = new List<string>();

        var tz = a.Str("timezone", "");
        if (tz.Length > 0)
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(tz); }   // validates the id before it reaches tzutil
            catch { return Response.Fail($"Unknown time zone '{tz}' on this computer."); }

            var psi = new ProcessStartInfo("tzutil.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add(tz);
            var r = await InstallHandler.RunAsync(psi, 30, ct);
            if (!r.Ok) return Response.Fail("Could not set the time zone: " + r.Error);
            notes.Add("time zone set");
        }

        if (mode == "ntp")
        {
            // start the Windows Time service if needed, then force a resync
            var psi = new ProcessStartInfo("cmd.exe", "/c \"sc start w32time >nul 2>&1 & w32tm /resync /force\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var r = await InstallHandler.RunAsync(psi, 60, ct);
            if (!r.Ok)
                return Response.Fail("Windows Time service could not sync (is there a time server reachable?). " + r.Error);
            notes.Add("resynced with the Windows time service");
        }
        else
        {
            long ticks = a.Long("utcTicks");
            if (ticks <= 0) return Response.Fail("Missing time.");
            var target = new DateTime(ticks, DateTimeKind.Utc);
            // the host stamped the time just before sending; a request takes only milliseconds to arrive
            var before = DateTime.UtcNow;

            if (!EnablePrivilege("SeSystemtimePrivilege"))
                return Response.Fail("This service may not change the system time.");

            var st = new SYSTEMTIME
            {
                Year = (ushort)target.Year, Month = (ushort)target.Month, DayOfWeek = (ushort)target.DayOfWeek,
                Day = (ushort)target.Day, Hour = (ushort)target.Hour, Minute = (ushort)target.Minute,
                Second = (ushort)target.Second, Milliseconds = (ushort)target.Millisecond,
            };
            if (!SetSystemTime(ref st))
                return Response.Fail($"Windows refused the time change (error {Marshal.GetLastWin32Error()}).");
            notes.Add($"clock moved by {(target - before).TotalSeconds:+0.0;-0.0} s");
        }

        Log.Write("time.sync: " + string.Join(", ", notes));
        return Response.Success(new JsonObject
        {
            ["utcTicks"] = DateTime.UtcNow.Ticks,
            ["note"] = string.Join(", ", notes),
        });
    }

    static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 /*ADJUST_PRIVILEGES*/ | 0x8 /*QUERY*/, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = 2 /*ENABLED*/ };
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                   && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }
}
