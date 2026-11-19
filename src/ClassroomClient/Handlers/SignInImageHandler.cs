// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// The lock and sign-in screen picture. Unlike the desktop wallpaper this is one machine-wide
/// setting, so it runs in the elevated service and works with nobody signed in. Windows reads it
/// from the PersonalizationCSP registry key (Pro, Education and Enterprise editions; Home ignores it).
/// </summary>
sealed class SignInImageHandler
{
    const string CspKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP";
    const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\Personalization";
    const string PathValue = "LockScreenImagePath";
    const string UrlValue = "LockScreenImageUrl";
    const string StatusValue = "LockScreenImageStatus";
    const long MaxImageBytes = 32L * 1024 * 1024;
    static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp"];

    static string ManagedDirectory => Path.Combine(ClientConfig.DataDir, "SignInImage");

    public async Task<Response> SetAsync(JsonObject a, CancellationToken ct)
    {
        try
        {
            if (Refusal() is { } refusal) return Response.Fail(refusal);

            var staging = Path.GetFullPath(ClientConfig.StagingDir).TrimEnd('\\');
            var source = Path.GetFullPath(a.Str("path"));
            if (!source.StartsWith(staging + "\\", StringComparison.OrdinalIgnoreCase))
                return Response.Fail("The image must be uploaded into the staging folder first.");
            var extension = Path.GetExtension(source).ToLowerInvariant();
            if (!Extensions.Contains(extension)) return Response.Fail("The sign-in screen image must be a JPG, PNG, or BMP file.");
            if (!File.Exists(source)) return Response.Fail("The uploaded image was not found in staging.");
            if (new FileInfo(source).Length > MaxImageBytes) return Response.Fail("The image is larger than 32 MB.");

            var bytes = await File.ReadAllBytesAsync(source, ct);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var expected = a.Str("sha256", "").Trim();
            if (expected.Length > 0 && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return Response.Fail("Checksum mismatch - the uploaded image is incomplete or was altered. Try again.");
            try
            {
                using var decoded = System.Drawing.Image.FromStream(new MemoryStream(bytes));
            }
            catch { return Response.Fail("That file could not be read as an image."); }

            // Windows caches the picture by path, so each distinct image gets its own file name.
            PrepareManagedDirectory();
            var target = Path.Combine(ManagedDirectory, $"signin-{hash[..16]}{extension}");
            await File.WriteAllBytesAsync(target, bytes, ct);

            using (var key = Machine().CreateSubKey(CspKey))
            {
                key.SetValue(PathValue, target, RegistryValueKind.String);
                key.SetValue(UrlValue, target, RegistryValueKind.String);
                key.SetValue(StatusValue, 1, RegistryValueKind.DWord);
            }
            DeleteManagedFiles(except: target);
            Log.Write($"signin.image.set: {target}");
            return Response.Success(new JsonObject { ["path"] = target });
        }
        catch (Exception ex) { return Response.Fail("Could not set the sign-in screen image: " + ex.Message); }
    }

    /// <summary>
    /// Removes the managed picture. On Windows Home there is none to remove, but leftovers are cleared all the
    /// same and the answer says it is Home, so the Host can reset the signed-in user's own picture instead.
    /// </summary>
    public Response UseDefault()
    {
        try
        {
            if (Refusal(forReset: true) is { } refusal) return Response.Fail(refusal);
            using (var key = Machine().OpenSubKey(CspKey, writable: true))
            {
                if (key is not null)
                    foreach (var value in new[] { PathValue, UrlValue, StatusValue })
                        key.DeleteValue(value, throwOnMissingValue: false);
            }
            DeleteManagedFiles(except: "");
            Log.Write("signin.image.default: managed sign-in image removed");
            return Response.Success(new JsonObject { [LockScreen.Home] = IsHome() });
        }
        catch (Exception ex) { return Response.Fail("Could not restore the Windows sign-in screen image: " + ex.Message); }
    }

    /// <summary>Why this computer cannot take (or, for a reset, give back) a managed sign-in image, or null when it can.</summary>
    static string? Refusal(bool forReset = false)
    {
        if (AgentContext.IsUserAgent) return "The sign-in screen image must be changed at the system level.";
        using var machine = Machine();
        using (var policy = machine.OpenSubKey(PolicyKey))
            if (policy?.GetValue("LockScreenImage") is string { Length: > 0 })
                return "A Group Policy sets the lock and sign-in screen image on this computer, so Classroom Manager cannot change it.";
        return !forReset && IsHome()
            ? "Windows Home does not support a managed lock and sign-in screen image. Use Windows default to reset the signed-in user's own lock screen picture."
            : null;
    }

    static bool IsHome()
    {
        using var windows = Machine().OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return (windows?.GetValue("EditionID") as string ?? "").StartsWith("Core", StringComparison.OrdinalIgnoreCase);
    }

    static RegistryKey Machine() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    /// <summary>The data folder is closed to standard users; the picture itself has to be readable by them.</summary>
    static void PrepareManagedDirectory() => ReadableByUsers(ManagedDirectory);

    /// <summary>Creates a folder below the closed data folder that every user of the computer may read.</summary>
    internal static void ReadableByUsers(string path)
    {
        var directory = Directory.CreateDirectory(path);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    static void DeleteManagedFiles(string except)
    {
        if (!Directory.Exists(ManagedDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(ManagedDirectory))
        {
            if (file.Equals(except, StringComparison.OrdinalIgnoreCase)) continue;
            // Windows may still hold the previous picture open; it is removed on a later change.
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
