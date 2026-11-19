// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

// T-222 MW-A: real production adapters and authenticated frames, synthetic handler only. No Windows appearance changes.
public sealed class AppearanceActionConnectionTests
{
    const string Key = "synthetic-appearance-actions-key";
    sealed class Rig : IAsyncDisposable
    {
        readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly List<Task> _connections = [];
        readonly Task _accepting;
        public ConcurrentQueue<(string Level, string Command, JsonObject Arguments)> Calls = new();
        public CancellationToken Token => _stop.Token;
        public ClientInfo Client => new() { Address = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port, Version = Protocol.Version, User = "fixture" };
        public Rig(bool home = false, string refused = "")
        {
            var server = new CommandServer(new ClientConfig { UseTls = false }, new KeyStore(Key, []), (request, _, _, _) =>
            {
                Calls.Enqueue((request.Level, request.Cmd, request.Args?.DeepClone().AsObject() ?? new()));
                return Task.FromResult(request.Cmd == refused ? Response.Fail("fixture refusal")
                    : Response.Success(new JsonObject { [LockScreen.Home] = home }));
            }, new PreAuthGate(new ConnectionLimits { FailuresBeforeBackoff = 100 }));
            _listener.Start();
            _accepting = Task.Run(async () =>
            {
                try
                {
                    while (!Token.IsCancellationRequested)
                    {
                        var tcp = await _listener.AcceptTcpClientAsync(Token);
                        lock (_connections) _connections.Add(server.HandleAsync(tcp, Token));
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _accepting;
            _listener.Stop();
            Task[] connections;
            lock (_connections) connections = [.. _connections];
            await Task.WhenAll(connections);
            _stop.Dispose();
        }
    }

    [Theory]
    [InlineData(WallpaperTarget.Desktop, false, false, Levels.User + ":" + Cmd.WallpaperDefault)]
    [InlineData(WallpaperTarget.SignInScreen, false, false, Levels.System + ":" + Cmd.SignInImageDefault)]
    [InlineData(WallpaperTarget.Both, false, false, Levels.User + ":" + Cmd.WallpaperDefault + "," + Levels.System + ":" + Cmd.SignInImageDefault)]
    [InlineData(WallpaperTarget.SignInScreen, false, true, Levels.User + ":" + Cmd.LockScreenReset)]
    [InlineData(WallpaperTarget.SignInScreen, true, false, Levels.System + ":" + Cmd.SignInImageDefault + "," + Levels.User + ":" + Cmd.LockScreenReset)]
    public async Task Wallpaper_default_preserves_user_machine_routing_and_windows_home_fallback(WallpaperTarget target, bool home, bool userOnly, string expected)
    {
        await using var rig = new Rig(home);
        var client = rig.Client;
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        await new ClientAppearanceActions(client, session, @"C:\synthetic-staging").ChangeWallpaperAsync(new(target, true, userOnly));
        Assert.Equal(expected.Split(','), rig.Calls.Select(call => call.Level + ":" + call.Command));
        await session.CallAsync(Cmd.Ping, ct: rig.Token); // Adapter never owns/disposes the session.
    }

    [Fact]
    public async Task Wallpaper_partial_failure_still_attempts_other_destination_and_reports_both_outcomes()
    {
        await using var rig = new Rig(refused: Cmd.WallpaperDefault);
        var client = rig.Client;
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ClientAppearanceActions(client, session, "fixture")
            .ChangeWallpaperAsync(new(WallpaperTarget.Both, true, false)));
        Assert.Contains("Desktop wallpaper:", error.Message);
        Assert.Contains("Sign-in screen: done", error.Message);
        Assert.Equal(new[] { Cmd.WallpaperDefault, Cmd.SignInImageDefault }, rig.Calls.Select(call => call.Command));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Screen_saver_routes_restore_or_installed_selection_and_system_display_in_order(bool restore)
    {
        await using var rig = new Rig();
        var client = rig.Client;
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        await new ClientAppearanceActions(client, session, "fixture").ChangeScreenSaverAsync(new(restore, false, "Close.scr", 300, true, null, 900));
        var calls = rig.Calls.ToArray();
        Assert.Equal(restore ? new[] { Cmd.PowerDisplaySet, Cmd.ScreenSaverReset } : new[] { Cmd.ScreenSaverSet, Cmd.PowerDisplaySet }, calls.Select(call => call.Command));
        Assert.Equal(restore ? new[] { Levels.System, Levels.User } : new[] { Levels.User, Levels.System }, calls.Select(call => call.Level));
        if (restore) Assert.True(calls[0].Arguments[DisplayPowerRules.Restore]!.GetValue<bool>());
        else
        {
            Assert.Equal("installed", calls[0].Arguments["mode"]!.GetValue<string>());
            Assert.Equal("Close.scr", calls[0].Arguments["name"]!.GetValue<string>());
            Assert.Equal(300, calls[0].Arguments["timeoutSec"]!.GetValue<int>());
            Assert.True(calls[0].Arguments["secure"]!.GetValue<bool>());
            Assert.Equal(900, calls[1].Arguments[DisplayPowerRules.Seconds]!.GetValue<int>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Language_install_is_system_and_preference_application_is_user_with_unchanged_arguments(bool install)
    {
        await using var rig = new Rig();
        var client = rig.Client;
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        var args = new JsonObject { ["theme"] = "dark", ["language"] = "lt-LT", ["resetLanguage"] = false, ["accentColor"] = "#123456" };
        await new ClientAppearanceActions(client, session, "fixture").ChangePreferencesAsync("lt-LT", install, args);
        var calls = rig.Calls.ToArray();
        Assert.Equal(install ? new[] { Cmd.LanguageInstall, Cmd.WindowsPreferencesSet } : new[] { Cmd.WindowsPreferencesSet }, calls.Select(call => call.Command));
        Assert.Equal(Levels.User, calls.Last().Level);
        Assert.True(JsonNode.DeepEquals(args, calls.Last().Arguments));
        if (install) Assert.Equal(Levels.System, calls[0].Level);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Windows_home_reset_requires_supported_command_and_signed_in_user(bool noUser)
    {
        await using var rig = new Rig(home: true);
        var client = rig.Client;
        if (noUser) client.User = "";
        else client.CapabilitiesAdvertised = true; // Explicit empty capability list.
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ClientAppearanceActions(client, session, "fixture")
            .ChangeWallpaperAsync(new(WallpaperTarget.SignInScreen, true, false)));
        Assert.Equal(Cmd.SignInImageDefault, Assert.Single(rig.Calls).Command);
    }

    [Fact]
    public async Task Failed_language_install_does_not_send_preferences()
    {
        await using var rig = new Rig(refused: Cmd.LanguageInstall);
        var client = rig.Client;
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        await Assert.ThrowsAsync<RemoteException>(() => new ClientAppearanceActions(client, session, "fixture")
            .ChangePreferencesAsync("lt-LT", true, new JsonObject { ["language"] = "lt-LT" }));
        Assert.Equal(Cmd.LanguageInstall, Assert.Single(rig.Calls).Command);
    }

    // Slice 2 pinning (Feathers): what the picture-sending dialogs do today, recorded before they move into the catalogue.
    static string Picture(string name = "Class Photo.PNG")
    {
        var folder = Directory.CreateTempSubdirectory("cm-wallpaper-pin-").FullName;
        var file = Path.Combine(folder, name);
        File.WriteAllBytes(file, [1, 2, 3, 4, 5]);
        return file;
    }

    static string Sha(string file) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    [Fact]
    public async Task A_desktop_picture_is_uploaded_under_a_fixed_staging_name_and_then_set_as_the_user()
    {
        await using var rig = new Rig();
        var client = rig.Client;
        var file = Picture();
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        await new ClientAppearanceActions(client, session, @"C:\staging").ChangeWallpaperAsync(new(WallpaperTarget.Desktop, false, false, file, "fill", Sha(file)));

        var calls = rig.Calls.ToArray();
        Assert.Equal([Cmd.FsWrite, Cmd.WallpaperSet], calls.Select(call => call.Command));
        Assert.Equal(@"C:\staging\wallpaper.png", calls[0].Arguments["path"]!.GetValue<string>());   // extension lower-cased, name fixed
        Assert.True(calls[0].Arguments["complete"]!.GetValue<bool>());
        Assert.Equal(Levels.User, calls[1].Level);
        Assert.Equal(@"C:\staging\wallpaper.png", calls[1].Arguments["path"]!.GetValue<string>());
        Assert.Equal("fill", calls[1].Arguments["style"]!.GetValue<string>());
        Assert.Equal("Class Photo.PNG", calls[1].Arguments["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_picture_for_both_places_is_uploaded_once_the_desktop_is_set_as_the_user_and_the_sign_in_image_as_the_system_with_its_checksum()
    {
        await using var rig = new Rig();
        var client = rig.Client;
        var file = Picture("a.jpg");
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        await new ClientAppearanceActions(client, session, @"C:\staging").ChangeWallpaperAsync(new(WallpaperTarget.Both, false, false, file, "fit", Sha(file)));

        var calls = rig.Calls.ToArray();
        Assert.Equal([Cmd.FsWrite, Cmd.WallpaperSet, Cmd.SignInImageSet], calls.Select(call => call.Command));
        Assert.Equal(Levels.User, calls[1].Level);
        Assert.Equal(Levels.System, calls[2].Level);
        Assert.Equal(@"C:\staging\wallpaper.jpg", calls[2].Arguments["path"]!.GetValue<string>());
        Assert.Equal(Sha(file), calls[2].Arguments["sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_lock_screen_picture_for_the_signed_in_user_only_goes_to_the_lock_screen_command_as_the_user()
    {
        await using var rig = new Rig();
        var client = rig.Client;
        var file = Picture("b.png");
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        await new ClientAppearanceActions(client, session, @"C:\staging").ChangeWallpaperAsync(new(WallpaperTarget.SignInScreen, false, true, file, "fill", Sha(file)));

        var calls = rig.Calls.ToArray();
        Assert.Equal([Cmd.FsWrite, Cmd.LockScreenSet], calls.Select(call => call.Command));
        Assert.Equal(Levels.User, calls[1].Level);
        Assert.Equal(@"C:\staging\wallpaper.png", calls[1].Arguments["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_picture_that_cannot_be_uploaded_changes_nothing()
    {
        await using var rig = new Rig(refused: Cmd.FsWrite);
        var client = rig.Client;
        var file = Picture("c.png");
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        await Assert.ThrowsAsync<RemoteException>(() => new ClientAppearanceActions(client, session, @"C:\staging")
            .ChangeWallpaperAsync(new(WallpaperTarget.Both, false, false, file, "fill", Sha(file))));

        Assert.Equal([Cmd.FsWrite], rig.Calls.Select(call => call.Command));
    }

    [Fact]
    public async Task Saver_off_sends_only_the_none_mode_without_an_unrequested_display_change()
    {
        await using var rig = new Rig();
        var client = rig.Client;
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        await new ClientAppearanceActions(client, session, "fixture").ChangeScreenSaverAsync(new(false, true, "", 0, false, null, null));
        var call = Assert.Single(rig.Calls);
        Assert.Equal(Cmd.ScreenSaverSet, call.Command);
        Assert.Equal(Levels.User, call.Level);
        Assert.Equal("none", call.Arguments["mode"]!.GetValue<string>());
        Assert.Single(call.Arguments);
    }
}
