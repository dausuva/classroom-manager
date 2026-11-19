// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Keeps the computer from going to sleep while the Host is moving files or running a long job on it. A computer woken by Wake-on-LAN with
/// nobody at it goes back to sleep after about two minutes (Windows' unattended sleep timeout), which cut a backup short on the test PC
/// (2026-10-25). Only the commands below count: the Host's status pings do not, so an open Host never keeps a room awake. Windows' own
/// timer runs again as soon as the work stops; the display is never kept on.
/// </summary>
static class SleepGuard
{
    /// <summary>Commands that move files or take minutes.</summary>
    static readonly HashSet<string> LongCommands = new(StringComparer.Ordinal)
    {
        Cmd.FsRead, Cmd.FsWrite, Cmd.FsRecent, Cmd.FsFind, Cmd.WindowsUpdateScan, Cmd.WindowsUpdateInstall, Cmd.InstallRun, Cmd.InstallWinget,
        Cmd.SelfUpdate, Cmd.InventoryGet,
    };

    public static bool KeepsAwake(string command) => LongCommands.Contains(command);

    static int _active;
    static System.Threading.Timer? _timer;
    static SafeFileHandle? _request;
    static long _lastTransfer;
    static readonly long TransferGrace = (long)(45 * System.Diagnostics.Stopwatch.Frequency);
    static readonly object Gate = new();

    /// <summary>How many long commands are running now.</summary>
    public static int Active => Volatile.Read(ref _active);

    /// <summary>Marks a command as running (dispose when it ends); null for a command that does not keep the computer awake.</summary>
    public static IDisposable? Enter(string command)
    {
        if (!KeepsAwake(command)) return null;
        lock (Gate)
        {
            _active++;
            if (command is Cmd.FsRead or Cmd.FsWrite)
                _lastTransfer = System.Diagnostics.Stopwatch.GetTimestamp();
            _request ??= CreateRequest();
            _timer ??= new System.Threading.Timer(_ => ReleaseIfIdle(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        return new Exit();
    }

    sealed class Exit : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (Gate) _active--;
            ReleaseIfIdle();
        }
    }

    // A handle-based system request survives async thread switches. Keep it between file chunks,
    // but release it after a 45-second inactivity grace. Pings never renew it.
    static void ReleaseIfIdle()
    {
        lock (Gate)
        {
            if (_active != 0 || (_lastTransfer != 0 && System.Diagnostics.Stopwatch.GetTimestamp() - _lastTransfer < TransferGrace)) return;
            if (_request is null) return;
            PowerClearRequest(_request, 1); // PowerRequestSystemRequired; never display/away mode.
            _request.Dispose();
            _request = null;
        }
    }

    internal static bool HasPowerRequest { get { lock (Gate) return _request is { IsInvalid: false, IsClosed: false }; } }

    static SafeFileHandle? CreateRequest()
    {
        var reason = Marshal.StringToHGlobalUni(typeof(SleepGuard).FullName!);
        try
        {
            var context = new ReasonContext { Flags = 1, Reason = new ReasonUnion { Simple = reason } };
            var handle = PowerCreateRequest(ref context);
            if (!handle.IsInvalid && PowerSetRequest(handle, 1)) return handle;
            handle.Dispose();
            return null;
        }
        finally { Marshal.FreeHGlobal(reason); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ReasonContext { public uint Version; public uint Flags; public ReasonUnion Reason; }
    [StructLayout(LayoutKind.Explicit)]
    struct ReasonUnion
    {
        [FieldOffset(0)] public IntPtr Simple;
        [FieldOffset(0)] public DetailedReason Detailed;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct DetailedReason { public IntPtr Module; public uint Id; public uint Count; public IntPtr Strings; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PowerSetRequest(SafeFileHandle handle, int type);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PowerClearRequest(SafeFileHandle handle, int type);
}
