// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClassroomManager.Host;

/// <summary>Applies the active app palette to the native Windows caption without replacing window chrome.</summary>
static class WindowsTitleBar
{
    const int UseImmersiveDarkMode = 20;
    const int UseImmersiveDarkModeBefore20H1 = 19;
    const int BorderColor = 34;
    const int CaptionColor = 35;
    const int TextColor = 36;
    const uint DefaultColor = 0xFFFFFFFF;

    public static void Apply(Window window, bool dark, bool highContrast)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            var useDark = !highContrast && dark ? 1 : 0;
            if (DwmSetInt(handle, UseImmersiveDarkMode, ref useDark, sizeof(int)) < 0)
                _ = DwmSetInt(handle, UseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));

            if (highContrast)
            {
                SetColor(handle, CaptionColor, DefaultColor);
                SetColor(handle, TextColor, DefaultColor);
                SetColor(handle, BorderColor, DefaultColor);
                return;
            }

            SetColor(handle, CaptionColor, ThemeColor("TitleBarBackgroundBrush", DefaultColor));
            SetColor(handle, TextColor, ThemeColor("TitleBarForegroundBrush", DefaultColor));
            SetColor(handle, BorderColor, ThemeColor("TitleBarBorderBrush", DefaultColor));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    static uint ThemeColor(string resource, uint fallback)
    {
        if (Application.Current?.TryFindResource(resource) is not SolidColorBrush brush) return fallback;
        var color = brush.Color;
        return color.R | ((uint)color.G << 8) | ((uint)color.B << 16);
    }

    static void SetColor(IntPtr handle, int attribute, uint value) =>
        _ = DwmSetUInt(handle, attribute, ref value, sizeof(uint));

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    static extern int DwmSetInt(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    static extern int DwmSetUInt(IntPtr hwnd, int attribute, ref uint value, int valueSize);
}
