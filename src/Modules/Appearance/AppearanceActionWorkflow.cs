// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Appearance;

public sealed record WallpaperChange(bool Upload, bool Desktop, bool SignIn);
public sealed record WallpaperChangeResult(string? DesktopError, string? SignInError);

public interface IWallpaperChangeRuntime
{
    Task UploadAsync();
    Task DesktopAsync();
    Task SignInAsync();
}

public sealed record ScreenSaverChange(bool Restore, bool Upload, int? DisplaySeconds, bool SupportsDisplayRestore);
public interface IScreenSaverChangeRuntime
{
    Task UploadAsync();
    Task SetAsync();
    Task RestoreAsync();
    Task RestoreDisplayAsync();
    Task SetDisplayAsync(int seconds);
}

public interface IWindowsPreferencesRuntime
{
    Task InstallLanguageAsync();
    Task ApplyAsync();
}

/// <summary>Trusted built-in sequencing over narrow core-owned action ports. No wire commands, paths, credentials or UI.</summary>
public static class AppearanceActionWorkflow
{
    public static async Task<WallpaperChangeResult> ChangeWallpaperAsync(WallpaperChange change, IWallpaperChangeRuntime runtime)
    {
        if (change.Upload) await runtime.UploadAsync();
        string? desktopError = null, signInError = null;
        // Both requested destinations are attempted even when one fails; the caller renders the partial result.
        if (change.Desktop) { try { await runtime.DesktopAsync(); } catch (Exception error) { desktopError = error.Message; } }
        if (change.SignIn) { try { await runtime.SignInAsync(); } catch (Exception error) { signInError = error.Message; } }
        return new(desktopError, signInError);
    }

    public static async Task ChangeScreenSaverAsync(ScreenSaverChange change, IScreenSaverChangeRuntime runtime)
    {
        if (change.Restore)
        {
            if (change.SupportsDisplayRestore) await runtime.RestoreDisplayAsync();
            await runtime.RestoreAsync();
            return;
        }
        if (change.Upload) await runtime.UploadAsync();
        await runtime.SetAsync();
        if (change.DisplaySeconds is { } seconds) await runtime.SetDisplayAsync(seconds);
    }

    public static async Task ChangePreferencesAsync(bool installLanguage, IWindowsPreferencesRuntime runtime)
    {
        if (installLanguage) await runtime.InstallLanguageAsync();
        await runtime.ApplyAsync();
    }
}
