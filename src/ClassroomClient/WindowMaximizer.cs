// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClassroomManager.Client;

/// <summary>
/// Maximizes the window a program has just opened, for a browser that starts a new window with no switch
/// to make it maximized (Firefox) or that hands the request to a copy that is already running and then
/// uses its own saved window size. Runs in the signed-in user's session, where those windows are.
/// </summary>
static class WindowMaximizer
{
    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);

    const uint GwOwner = 4;
    const int SwMaximize = 3;

    /// <summary>The windows of programs with this process name that a person would call an application window: visible, with a title and no owner.</summary>
    public static HashSet<IntPtr> Windows(string processName)
    {
        var found = new HashSet<IntPtr>();
        var names = new Dictionary<uint, bool>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, GwOwner) != IntPtr.Zero || GetWindowTextLength(window) == 0) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (!names.TryGetValue(processId, out var wanted)) names[processId] = wanted = IsProcess(processId, processName);
            if (wanted) found.Add(window);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a window that was not in <paramref name="before"/> and maximizes it.
    /// When the program opened the address in a window that was already there, that one is maximized if it is in front.
    /// Returns whether a window was maximized.
    /// </summary>
    public static async Task<bool> MaximizeNewAsync(string processName, HashSet<IntPtr> before, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var fresh = Windows(processName).Where(window => !before.Contains(window)).ToList();
            if (fresh.Count > 0)
            {
                // A browser draws its window in two steps; give it a moment before asking for the final size.
                await Task.Delay(300, ct);
                return fresh.Count(Maximize) > 0;
            }
            await Task.Delay(150, ct);
        }

        var front = GetForegroundWindow();
        return front != IntPtr.Zero && Windows(processName).Contains(front) && Maximize(front);
    }

    static bool Maximize(IntPtr window)
    {
        if (IsZoomed(window)) return true;
        ShowWindow(window, SwMaximize);
        SetForegroundWindow(window);
        return IsZoomed(window);
    }

    static bool IsProcess(uint processId, string processName)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }
}
