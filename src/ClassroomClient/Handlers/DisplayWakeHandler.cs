// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Stops a running screen saver and turns the display back on (runs in the per-user helper, because
/// both belong to the user's session). The screen saver is a program of that same user, so it is
/// closed like any other; the pointer is then moved one pixel and back, which is what makes Windows
/// light the display and restart its idle timers. With "sign-in on resume" Windows shows its lock
/// screen once the screen saver is gone, and nothing here gets past that.
/// </summary>
sealed class DisplayWakeHandler
{
    const uint SPI_GETSCREENSAVERRUNNING = 0x0072;
    const uint INPUT_MOUSE = 0;
    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint ES_SYSTEM_REQUIRED = 0x00000001;
    const uint ES_DISPLAY_REQUIRED = 0x00000002;
    const uint DESKTOP_READOBJECTS = 0x0001;
    const int UOI_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformation(IntPtr handle, int index, char[] buffer, int bytes, out int needed);

    [DllImport("user32.dll")]
    static extern bool CloseDesktop(IntPtr desktop);

    public async Task<Response> WakeAsync(CancellationToken ct)
    {
        try
        {
            bool running = SystemParametersInfo(SPI_GETSCREENSAVERRUNNING, 0, out var flag, 0) && flag != 0;
            int stopped = running ? await StopScreenSaversAsync(ct) : 0;

            // Restarts the idle timers even where injected input is not accepted (the lock screen).
            SetThreadExecutionState(ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
            bool inputSent = MovePointer(1) & MovePointer(-1);

            // Windows needs a moment to switch to its lock screen after a secure screen saver ends.
            if (stopped > 0) await Task.Delay(500, ct);
            bool signInScreen = !UserDesktopHasInput();
            Log.Write($"display.wake: screen saver {(running ? $"running, {stopped} stopped" : "not running")}, " +
                      $"input {(inputSent ? "sent" : "refused")}, {(signInScreen ? "lock screen" : "desktop")}.");
            return Response.Success(new JsonObject
            {
                [DisplayWakeRules.ScreenSaverWasRunning] = running,
                [DisplayWakeRules.SignInScreen] = signInScreen,
                ["stopped"] = stopped,
                ["inputSent"] = inputSent,
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Response.Fail("Could not wake the screen: " + ex.Message); }
    }

    /// <summary>Turns the display on as a key press would, for a caller that Windows refuses while the display is off.</summary>
    public static void WakeDisplay()
    {
        try
        {
            SetThreadExecutionState(ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
            MovePointer(1);
            MovePointer(-1);
        }
        catch (Exception ex) { Log.Write($"display.wake: could not wake the display: {ex.Message}"); }
    }

    /// <summary>Stops a running screen saver, for a caller that cannot change a setting while one runs. True when one was running.</summary>
    public static bool StopRunningScreenSaver()
    {
        try
        {
            if (!(SystemParametersInfo(SPI_GETSCREENSAVERRUNNING, 0, out var flag, 0) && flag != 0)) return false;
            StopScreenSaversAsync(CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"display.wake: could not stop the screen saver: {ex.Message}");
            return false;
        }
    }

    /// <summary>Closes every screen saver program of this session; one that ignores the request is ended.</summary>
    static async Task<int> StopScreenSaversAsync(CancellationToken ct)
    {
        int session = Process.GetCurrentProcess().SessionId;
        int stopped = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session || !IsScreenSaver(process)) continue;
                    process.CloseMainWindow();
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    limit.CancelAfter(TimeSpan.FromSeconds(1));
                    try { await process.WaitForExitAsync(limit.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { process.Kill(); }
                    stopped++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Already gone, or a process of another user in this session: not ours to stop.
                }
            }
        }
        return stopped;
    }

    static bool IsScreenSaver(Process process)
    {
        if (process.ProcessName.EndsWith(ScreenSaverRules.Extension, StringComparison.OrdinalIgnoreCase)) return true;
        try { return process.MainModule?.FileName.EndsWith(ScreenSaverRules.Extension, StringComparison.OrdinalIgnoreCase) == true; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    static bool MovePointer(int dx)
    {
        var inputs = new[] { new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = dx, dwFlags = MOUSEEVENTF_MOVE } } };
        return SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1;
    }

    /// <summary>False while Windows shows a desktop of its own: the lock screen, sign-in or a secure screen saver.</summary>
    static bool UserDesktopHasInput()
    {
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new char[64];
            return GetUserObjectInformation(desktop, UOI_NAME, name, name.Length * sizeof(char), out _) &&
                   new string(name).TrimEnd('\0').Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }
}
