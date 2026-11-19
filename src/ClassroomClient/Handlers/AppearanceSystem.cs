// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClassroomManager.Client;

/// <summary>The signed-in user's own registry hive. Only the values the catalogue names are ever touched.</summary>
sealed class RegistryAppearanceStore : IAppearanceStore
{
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    static extern bool SystemParametersInfoGet(uint action, uint param, ref int value, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    static extern bool SystemParametersInfoSet(uint action, uint param, IntPtr value, uint winIni);

    const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    const uint SPI_SETCLIENTAREAANIMATION = 0x1043;
    const uint SPIF_UPDATEINIFILE_SENDCHANGE = 0x03;

    public RawValue? Read(string path, string name)
    {
        if (path == AppearanceSettings.DisplaySizePath) return RawValue.Str(DisplayModes.Size());
        if (path == AppearanceSettings.DisplayOrientationPath) return RawValue.Dword(DisplayModes.Orientation());
        if (path == AppearanceSettings.AnimationsPath)
        {
            var on = 0;
            return SystemParametersInfoGet(SPI_GETCLIENTAREAANIMATION, 0, ref on, 0) ? RawValue.Dword(on != 0 ? 1 : 0) : null;
        }
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            int number => RawValue.Dword(number),
            string text => RawValue.Str(text),
            _ => null,
        };
    }

    public void Write(string path, string name, RawValue value)
    {
        // Applied for now only; the handler asks the person at the computer and then calls Persist, or goes back.
        if (path == AppearanceSettings.DisplaySizePath) { DisplayModes.SetSize(value.Text ?? "", store: false); return; }
        if (path == AppearanceSettings.DisplayOrientationPath) { DisplayModes.SetOrientation(value.Number ?? 0, store: false); return; }
        if (path == AppearanceSettings.AnimationsPath)
        {
            if (!SystemParametersInfoSet(SPI_SETCLIENTAREAANIMATION, 0, new IntPtr(value.Number is 0 ? 0 : 1), SPIF_UPDATEINIFILE_SENDCHANGE))
                throw new IOException("Windows refused to change the animation effects.");
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(path);
        if (value.Number is { } number) key.SetValue(name, number, RegistryValueKind.DWord);
        else key.SetValue(name, value.Text ?? "", RegistryValueKind.String);
    }

    public void Persist(string path, string name)
    {
        if (path.StartsWith("display:", StringComparison.Ordinal)) DisplayModes.Keep();
    }

    public void Delete(string path, string name)
    {
        // A setting kept behind a system call always has a value, so there is nothing to remove.
        if (path == AppearanceSettings.AnimationsPath || path.StartsWith("display:", StringComparison.Ordinal)) return;
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>Windows' own ways of being told that a setting changed: a broadcast, a redraw of the desktop, and laying the wallpaper out again.</summary>
sealed class WindowsAppearanceShell : IAppearanceShell
{
    const uint WM_SETTINGCHANGE = 0x001A;
    const uint WM_COMMAND = 0x0111;
    const uint SMTO_ABORTIFHUNG = 0x0002;
    const uint SPI_SETDESKWALLPAPER = 0x0014;
    const uint SPIF_UPDATEINIFILE = 0x01;
    const uint SPIF_SENDCHANGE = 0x02;
    static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    // Commands of the desktop's folder view (the window that holds the icons): F5 redraws the icons; the other one toggles
    // "Show desktop icons" (and Explorer writes the HideIcons value itself), so it is sent only when the icons are not as they should be.
    const int RefreshCommand = 0x7103;
    const int ToggleIconsCommand = 0x7402;

    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string? className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    static extern bool SystemParametersInfo(uint action, uint param, string? vparam, uint winIni);

    public void Refresh(IReadOnlySet<AppearanceRefresh> needed)
    {
        if (needed.Contains(AppearanceRefresh.Wallpaper)) LayOutWallpaperAgain();
        if (needed.Contains(AppearanceRefresh.Theme)) Broadcast("ImmersiveColorSet");
        // The taskbar reads its own settings again: Windows 10 does not watch them, Windows 11 does and ignores the extra message.
        if (needed.Contains(AppearanceRefresh.Tray)) Broadcast("TraySettings");
        if (needed.Contains(AppearanceRefresh.Desktop))
        {
            Broadcast("ShellState");
            RefreshDesktop();
        }
    }

    static void Broadcast(string area) =>
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, area, SMTO_ABORTIFHUNG, 200, out _);

    static void LayOutWallpaperAgain()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        var wallpaper = key?.GetValue("Wallpaper") as string ?? "";
        // Setting the picture Windows already shows makes it lay it out with the new fit.
        SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, wallpaper, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    static void RefreshDesktop()
    {
        var view = FindDesktopView();
        if (view == IntPtr.Zero) return;
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
        {
            var wanted = key?.GetValue("HideIcons") is not int hidden || hidden == 0;
            var list = FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            if (list != IntPtr.Zero && IsWindowVisible(list) != wanted) SendMessage(view, WM_COMMAND, new IntPtr(ToggleIconsCommand), IntPtr.Zero);
        }
        SendMessage(view, WM_COMMAND, new IntPtr(RefreshCommand), IntPtr.Zero);
    }

    /// <summary>The window that holds the desktop icons: under Progman, or under a WorkerW when a wallpaper slideshow is running.</summary>
    internal static IntPtr FindDesktopView()
    {
        var progman = FindWindow("Progman", null);
        var view = progman == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero) return view;
        var found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            var child = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child == IntPtr.Zero) return true;
            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
