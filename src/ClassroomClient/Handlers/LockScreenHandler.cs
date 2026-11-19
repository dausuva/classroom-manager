// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// The signed-in user's own lock screen picture (lockscreen.reset). Windows Home has no machine-wide
/// lock and sign-in screen picture (see <see cref="SignInImageHandler"/>): each user chooses one in
/// Settings > Personalization > Lock screen, and the sign-in screen shows it. Resetting switches that
/// user back to Windows spotlight, which is what Windows starts with. What was set before is kept, so
/// "restore" can put it back.
/// </summary>
sealed class LockScreenHandler
{
    const string ContentKey = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    const string LockKey = @"Software\Microsoft\Windows\CurrentVersion\Lock Screen";

    /// <summary>The values a reset writes: Windows spotlight on, with its facts overlay, and no slideshow.</summary>
    static readonly (string Key, string Name, int Spotlight)[] Values =
    [
        (ContentKey, "RotatingLockScreenEnabled", 1),
        (ContentKey, "RotatingLockScreenOverlayEnabled", 1),
        (LockKey, "SlideshowEnabled", 0),
    ];

    static string StatePath => Path.Combine(ClientConfig.UserDir, "lockscreen-state.json");

    public Response Reset(JsonObject a)
    {
        try
        {
            if (a.Bool(LockScreen.Restore) == true) return Restore();
            // Remembered once: a second reset must not record spotlight as what the student had.
            if (!File.Exists(StatePath))
            {
                var before = Values.ToDictionary(value => $@"{value.Key}\{value.Name}", value => Read(value.Key, value.Name));
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(before));
            }
            foreach (var (key, name, spotlight) in Values)
                using (var open = Registry.CurrentUser.CreateSubKey(key))
                    open.SetValue(name, spotlight, RegistryValueKind.DWord);
            Log.Write("lockscreen.reset: Windows spotlight turned on for the lock screen");
            return Response.Success(new JsonObject { [LockScreen.Spotlight] = true });
        }
        catch (Exception ex) { return Response.Fail("Could not reset the lock screen picture: " + ex.Message); }
    }

    static string PictureDirectory => Path.Combine(ClientConfig.UserDir, "LockScreen");
    static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp"];

    /// <summary>
    /// Makes an uploaded picture the signed-in user's own lock screen (lockscreen.set), on every edition
    /// including Home. The picture is copied into this user's folder first: the upload is removed afterwards,
    /// and Windows reads the file it was given.
    /// </summary>
    public Response Set(JsonObject a)
    {
        try
        {
            var source = Path.GetFullPath(a.Str("path"));
            var staging = Path.GetFullPath(ClientConfig.StagingDir).TrimEnd('\\') + "\\";
            if (!source.StartsWith(staging, StringComparison.OrdinalIgnoreCase)) return Response.Fail("The picture must be uploaded into the staging folder first.");
            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (!Extensions.Contains(extension)) return Response.Fail("The lock screen picture must be a JPG, PNG, or BMP file.");
            if (!File.Exists(source)) return Response.Fail("The uploaded picture was not found in staging.");

            Directory.CreateDirectory(PictureDirectory);
            var target = Path.Combine(PictureDirectory, $"lockscreen-{DateTime.UtcNow:yyyyMMddHHmmss}{extension}");
            File.Copy(source, target, overwrite: true);
            WinRtLockScreen.SetImage(target, TimeSpan.FromSeconds(30));
            // Earlier pictures are no longer used; Windows keeps its own copy of the one it shows.
            foreach (var old in Directory.EnumerateFiles(PictureDirectory).Where(file => !file.Equals(target, StringComparison.OrdinalIgnoreCase)))
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Log.Write($"lockscreen.set: {Path.GetFileName(source)} is the signed-in user's lock screen picture");
            return Response.Success(new JsonObject { ["path"] = target });
        }
        catch (Exception ex) { return Response.Fail("Could not set the lock screen picture: " + ex.Message); }
    }

    Response Restore()
    {
        if (!File.Exists(StatePath)) return Response.Success(new JsonObject { [LockScreen.Restored] = false });
        var before = JsonSerializer.Deserialize<Dictionary<string, int?>>(File.ReadAllText(StatePath)) ?? [];
        foreach (var (key, name, _) in Values)
        {
            using var open = Registry.CurrentUser.CreateSubKey(key);
            if (before.GetValueOrDefault($@"{key}\{name}") is { } number) open.SetValue(name, number, RegistryValueKind.DWord);
            else open.DeleteValue(name, throwOnMissingValue: false);
        }
        File.Delete(StatePath);
        Log.Write("lockscreen.reset: the earlier lock screen settings were put back");
        return Response.Success(new JsonObject { [LockScreen.Restored] = true });
    }

    static int? Read(string key, string name)
    {
        using var open = Registry.CurrentUser.OpenSubKey(key);
        return open?.GetValue(name) is int number ? number : null;
    }
}
