// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LivePicturePortPathFactAttribute : FactAttribute
{
    public LivePicturePortPathFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_APPEARANCE_PORT_TEST_ADDRESS")))
            Skip = "Opt-in: changes and then restores the wallpaper, lock screen and sign-in picture of one installed test Client through the Appearance module's upload ports.";
    }
}

/// <summary>
/// A picture sent to a real Client through the module's upload ports (<c>storage.source</c>, <c>client.upload</c>): the file arrives intact in the
/// staging folder, Windows reports it as the desktop wallpaper, the sign-in picture is written where Windows reads it, and everything is put back.
/// </summary>
[Collection(nameof(HostBackupTests))]
public sealed class LivePicturePortPathTests(ITestOutputHelper output)
{
    const string WallpaperProbe = """
        Add-Type @"
        using System; using System.Runtime.InteropServices;
        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDesktopWallpaper {
          void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
          [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
        }
        [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")] public class DesktopWallpaperClass { }
        public static class WallpaperProbe { public static string Path() { return ((IDesktopWallpaper)new DesktopWallpaperClass()).GetWallpaper(null); } }
        "@
        $p = [WallpaperProbe]::Path()
        $h = if ($p -and (Test-Path -LiteralPath $p)) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLower() } else { '' }
        "wp=$p|$h"
        """;

    const string SignInProbe = """
        $k = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP' -ErrorAction SilentlyContinue
        $p = if ($k) { $k.LockScreenImagePath } else { '' }
        $h = if ($p -and (Test-Path -LiteralPath $p)) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLower() } else { '' }
        "signin=$p|$h"
        """;

    // What Windows itself says the signed-in user's lock screen picture is (Windows.System.UserProfile.LockScreen.OriginalImageFile).
    const string LockScreenProbe = """
        [void][Windows.System.UserProfile.LockScreen,Windows.System.UserProfile,ContentType=WindowsRuntime]
        $u = [Windows.System.UserProfile.LockScreen]::OriginalImageFile
        $p = if ($u) { $u.LocalPath } else { '' }
        $h = if ($p -and (Test-Path -LiteralPath $p)) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLower() } else { '' }
        "lock=$p|$h"
        """;

    static string MakePicture(string folder, byte red)
    {
        var pixels = new byte[64 * 64 * 3];
        for (var index = 0; index < pixels.Length; index += 3) { pixels[index] = 40; pixels[index + 1] = 80; pixels[index + 2] = red; }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgr24, null, pixels, 64 * 3)));
        var path = Path.Combine(folder, $"live-picture-{red}.png");
        using (var stream = File.Create(path)) encoder.Save(stream);
        return path;
    }

    static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [LivePicturePortPathFact]
    public async Task A_picture_goes_through_the_ports_becomes_the_wallpaper_and_the_sign_in_picture_and_is_put_back()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_APPEARANCE_PORT_TEST_ADDRESS")!;
        var settingsPath = Environment.GetEnvironmentVariable("CLASSROOM_BACKUP_TEST_SETTINGS");
        Assert.False(string.IsNullOrWhiteSpace(settingsPath));
        var before = File.ReadAllBytes(settingsPath!);
        var keysPath = Path.ChangeExtension(settingsPath!, ".keys");
        var keysBefore = File.ReadAllBytes(keysPath);
        var folder = Directory.CreateTempSubdirectory("cm-live-picture-").FullName;
        var suspended = HostSettings.SavesSuspended;
        HostSettings.SavesSuspended = true;
        ClientSession? session = null;
        string? originalWallpaper = null, originalSignIn = null;
        var completed = false;
        try
        {
            var settings = HostSettings.Load(settingsPath);
            var saved = Assert.Single(settings.Computers, c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            var client = new ClientInfo { Address = saved.Address, Port = saved.Port, ClientId = saved.ClientId, Mac = saved.Mac, KeyId = saved.KeyId, ComputerName = saved.Name };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            session = await ClientSession.OpenAsync(client, settings, deadline.Token);
            var ping = await session.CallAsync(Cmd.Ping, ct: deadline.Token);
            client.Version = ping["version"]?.GetValue<string>() ?? "";
            client.User = ping["userName"]?.GetValue<string>() ?? "";
            Assert.NotEmpty(client.User);
            var staging = StagingNamespace.Root(settings);
            var actions = new ClientAppearanceActions(client, session, staging);

            originalWallpaper = await Probe(session, WallpaperProbe, "wp=", deadline.Token);
            originalSignIn = await Probe(session, SignInProbe, "signin=", deadline.Token, system: true);
            output.WriteLine($"{client.Name} (client {client.Version}): wallpaper before [{originalWallpaper}]; sign-in before [{originalSignIn}]");

            // The desktop wallpaper: Windows itself must report a file with the same bytes as the picture the teacher picked.
            var picture = MakePicture(folder, 200);
            await actions.ChangeWallpaperAsync(new(WallpaperTarget.Desktop, false, false, picture, "fill", Sha(picture)));
            await Task.Delay(1500, deadline.Token);
            var shown = await Probe(session, WallpaperProbe, "wp=", deadline.Token);
            output.WriteLine($"wallpaper after [{shown}]");
            Assert.NotEqual(originalWallpaper, shown);
            Assert.EndsWith("|" + Sha(picture), shown);

            // The sign-in picture, machine-wide. Windows Home does not take one; that refusal is the right answer there.
            var second = MakePicture(folder, 90);
            string signInResult;
            try
            {
                await actions.ChangeWallpaperAsync(new(WallpaperTarget.SignInScreen, false, false, second, "fill", Sha(second)));
                var written = await Probe(session, SignInProbe, "signin=", deadline.Token, system: true);
                output.WriteLine($"sign-in after [{written}]");
                Assert.EndsWith("|" + Sha(second), written);
                signInResult = "set and read back";
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Windows Home") || ex.Message.Contains("Group Policy"))
            {
                signInResult = "refused by this computer: " + ex.Message;
            }
            output.WriteLine($"Sign-in picture: {signInResult}");

            // The signed-in user's own lock screen picture works on every edition.
            await actions.ChangeWallpaperAsync(new(WallpaperTarget.SignInScreen, false, true, second, "fill", Sha(second)));
            await Task.Delay(1500, deadline.Token);
            var lockShown = await Probe(session, LockScreenProbe, "lock=", deadline.Token);
            output.WriteLine($"Lock screen picture of the signed-in user: set; Windows reports [{lockShown}]");
            Assert.EndsWith("|" + Sha(second), lockShown);
            completed = true;
        }
        finally
        {
            if (session is not null)
            {
                // Put everything back, whatever happened above.
                foreach (var undo in new Func<Task>[]
                {
                    () => session.CallAtAsync(Levels.User, Cmd.WallpaperReset),
                    () => session.CallAtAsync(Levels.User, Cmd.LockScreenReset),
                    () => session.CallAsync(Cmd.SignInImageDefault),
                })
                    try { await undo(); } catch (Exception ex) { output.WriteLine("restore: " + ex.Message); }
                if (originalWallpaper is not null && originalSignIn is not null)
                {
                    using var check = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                    await Task.Delay(1500);
                    var wallpaperNow = await Probe(session, WallpaperProbe, "wp=", check.Token);
                    var signInNow = await Probe(session, SignInProbe, "signin=", check.Token, system: true);
                    output.WriteLine($"after restore: wallpaper [{wallpaperNow}]; sign-in [{signInNow}]");
                    if (completed)
                    {
                        Assert.Equal(originalWallpaper, wallpaperNow);
                        Assert.Equal(originalSignIn, signInNow);
                    }
                }
                session.Dispose();
            }
            HostSettings.SavesSuspended = suspended;
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
            Assert.Equal(before, File.ReadAllBytes(settingsPath!));
            Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
        }
    }

    static async Task<string> Probe(ClientSession session, string script, string prefix, CancellationToken cancellation, bool system = false)
    {
        var data = await session.CallAtAsync(system ? Levels.System : Levels.User, Cmd.CmdRun,
            new JsonObject { ["shell"] = "powershell", ["command"] = script, ["timeoutSec"] = 60 }, cancellation);
        Assert.False(data["timedOut"]?.GetValue<bool>() == true, "the probe timed out");
        var line = (data["output"]?.GetValue<string>() ?? "").Split((char)10).Select(item => item.Trim()).FirstOrDefault(item => item.StartsWith(prefix)) ?? "";
        return line[prefix.Length..];
    }
}
