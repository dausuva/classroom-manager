// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Restores common per-user Explorer desktop and taskbar layout defaults.</summary>
sealed class DesktopLayoutHandler
{
    const string DesktopBag = @"Software\Microsoft\Windows\Shell\Bags\1\Desktop";
    const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string StuckRects3 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";
    const int FolderFlagAutoArrange = 0x1;
    const int FolderFlagSnapToGrid = 0x4;

    public async Task<Response> ResetAsync(JsonObject args, CancellationToken ct)
    {
        var resetIconSize = args["iconSize"]?.GetValue<bool>() ?? false;
        var arrangeIcons = args["arrangeIcons"]?.GetValue<bool>() ?? false;
        var resetTaskbar = args["taskbar"]?.GetValue<bool>() ?? false;
        if (!resetIconSize && !arrangeIcons && !resetTaskbar)
            return Response.Fail("Choose at least one desktop or taskbar setting to reset.");

        var changed = new List<string>();
        try
        {
            byte[]? legacyTaskbarSettings = null;
            if (resetTaskbar && Environment.OSVersion.Version.Build < 22000)
            {
                using var taskbar = Registry.CurrentUser.OpenSubKey(StuckRects3);
                if (taskbar?.GetValue("Settings") is not byte[] settings || settings.Length <= 12)
                    return Response.Fail("Windows did not expose a taskbar layout that can be reset for this user.");
                legacyTaskbarSettings = settings;
            }

            if (resetIconSize || arrangeIcons)
            {
                using var desktop = Registry.CurrentUser.CreateSubKey(DesktopBag);
                if (resetIconSize)
                {
                    desktop.SetValue("IconSize", 48, RegistryValueKind.DWord);
                    desktop.SetValue("LogicalViewMode", 3, RegistryValueKind.DWord);
                    desktop.SetValue("Mode", 1, RegistryValueKind.DWord);
                    changed.Add("desktop icons set to medium size");
                }
                if (arrangeIcons)
                {
                    var flags = desktop.GetValue("FFlags") is int current ? current : 0;
                    desktop.SetValue("FFlags", flags | FolderFlagAutoArrange | FolderFlagSnapToGrid,
                        RegistryValueKind.DWord);
                    changed.Add("desktop icons auto-arranged and snapped to grid");
                }
            }

            if (resetTaskbar)
            {
                if (Environment.OSVersion.Version.Build >= 22000)
                {
                    using var advanced = Registry.CurrentUser.CreateSubKey(ExplorerAdvanced);
                    advanced.SetValue("TaskbarAl", 1, RegistryValueKind.DWord);
                    changed.Add("taskbar restored to the Windows 11 default bottom/centered layout");
                }
                else
                {
                    using var taskbar = Registry.CurrentUser.OpenSubKey(StuckRects3, writable: true);
                    legacyTaskbarSettings![12] = 3;
                    taskbar!.SetValue("Settings", legacyTaskbarSettings, RegistryValueKind.Binary);
                    changed.Add("taskbar moved to the bottom edge");
                }
            }

            var restart = await RestartExplorerAsync(ct);
            if (!restart.Ok) return restart;
            return Response.Success(new JsonObject { ["summary"] = string.Join(", ", changed) });
        }
        catch (Exception ex) { return Response.Fail("Could not reset the desktop layout: " + ex.Message); }
    }

    static async Task<Response> RestartExplorerAsync(CancellationToken ct)
    {
        try
        {
            var sessionId = Process.GetCurrentProcess().SessionId;
            foreach (var explorer in Process.GetProcessesByName("explorer").Where(process => process.SessionId == sessionId))
            {
                using (explorer)
                {
                    explorer.Kill(entireProcessTree: true);
                    await explorer.WaitForExitAsync(ct);
                }
            }
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            return Response.Success();
        }
        catch (Exception ex) { return Response.Fail("The settings were saved, but Explorer could not be restarted: " + ex.Message); }
    }
}
