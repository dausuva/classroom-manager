// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>Where a chosen background goes: the signed-in user's desktop, the machine-wide lock and sign-in screen, or both.</summary>
public enum WallpaperTarget { Desktop, SignInScreen, Both }

public static class WallpaperTargets
{
    public const string DesktopLabel = "Desktop wallpaper";
    public const string SignInLabel = "Lock and sign-in screen";
    public const string BothLabel = "Desktop and sign-in screen";

    public static readonly string[] Labels = [DesktopLabel, SignInLabel, BothLabel];

    public static WallpaperTarget FromLabel(string? label) => label switch
    {
        SignInLabel => WallpaperTarget.SignInScreen,
        BothLabel => WallpaperTarget.Both,
        _ => WallpaperTarget.Desktop,
    };

    public static bool IncludesDesktop(this WallpaperTarget target) => target != WallpaperTarget.SignInScreen;

    public static bool IncludesSignIn(this WallpaperTarget target) => target != WallpaperTarget.Desktop;

    /// <summary>Operation title for the Operations view and log, e.g. "Set wallpaper and sign-in screen image".</summary>
    public static string OperationTitle(WallpaperTarget target, bool windowsDefault) => (target, windowsDefault) switch
    {
        (WallpaperTarget.Desktop, false) => "Set wallpaper",
        (WallpaperTarget.SignInScreen, false) => "Set sign-in screen image",
        (WallpaperTarget.Both, false) => "Set wallpaper and sign-in screen image",
        (WallpaperTarget.Desktop, true) => "Set Windows default wallpaper",
        (WallpaperTarget.SignInScreen, true) => "Restore Windows sign-in screen image",
        _ => "Restore Windows default wallpaper and sign-in screen image",
    };

    /// <summary>
    /// One computer's result when the two places are changed independently: empty when everything
    /// requested succeeded, otherwise which place failed and why, noting any place that did succeed.
    /// </summary>
    public static string FailureMessage(WallpaperTarget target, string? desktopError, string? signInError)
    {
        var parts = new List<string>();
        if (target.IncludesDesktop())
            parts.Add(desktopError is null ? "Desktop wallpaper: done" : $"Desktop wallpaper: {desktopError}");
        if (target.IncludesSignIn())
            parts.Add(signInError is null ? "Sign-in screen: done" : $"Sign-in screen: {signInError}");
        return desktopError is null && signInError is null ? "" : string.Join("; ", parts);
    }
}
