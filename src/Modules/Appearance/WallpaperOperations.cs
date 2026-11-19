// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Modules;

namespace ClassroomManager.Appearance;

/// <summary>What the teacher asked for when changing the background of one computer.</summary>
/// <param name="Desktop">Change the signed-in user's desktop wallpaper.</param>
/// <param name="SignIn">Change the lock screen: the signed-in user's own, or the one every user sees (see <paramref name="UserOnly"/>).</param>
/// <param name="WindowsDefault">Put Windows' own picture back instead of sending one.</param>
/// <param name="UserOnly">The lock screen picture is the signed-in user's own and works on every edition of Windows, Home included.</param>
/// <param name="Picture">The picture the teacher picked; required unless <paramref name="WindowsDefault"/>.</param>
/// <param name="Style">How the wallpaper fills the screen (fill, fit, stretch, tile, center, span).</param>
/// <param name="CanResetLockScreen">The computer's Client has the command that puts a user's own lock screen back; the Host knows from the capability announcement.</param>
/// <param name="HasUser">Somebody is signed in on the computer.</param>
public sealed record WallpaperRequest(bool Desktop, bool SignIn, bool WindowsDefault, bool UserOnly, ModulePickedFile? Picture, string Style, bool CanResetLockScreen, bool HasUser);

/// <summary>
/// Changing the desktop wallpaper, the lock screen and the sign-in screen picture through the run's ports: one upload when a picture is sent,
/// then the commands for the places asked for. The sign-in screen picture belongs to every user of the computer, so it is the one command sent
/// with administrator rights; the two others act for the signed-in user.
/// </summary>
public sealed class WallpaperOperations(IModuleClient client, IModuleUploads uploads)
{
    public const string WallpaperSet = "wallpaper.set";
    public const string WallpaperDefault = "wallpaper.default";
    public const string LockScreenSet = "lockscreen.set";
    public const string LockScreenReset = "lockscreen.reset";
    public const string SignInSet = "signin.image.set";
    public const string SignInDefault = "signin.image.default";

    /// <summary>The key of the answer that tells a computer is Windows Home, where the sign-in picture cannot be managed.</summary>
    const string HomeKey = "home";

    public static IReadOnlyList<string> Commands { get; } = [WallpaperSet, WallpaperDefault, LockScreenSet, LockScreenReset, SignInSet, SignInDefault];

    /// <summary>The commands that change something for every user of the computer.</summary>
    public static IReadOnlyList<string> SystemCommands { get; } = [SignInSet, SignInDefault];

    /// <summary>Changes the places asked for and reports each place on its own: one can work while the other fails.</summary>
    public Task<WallpaperChangeResult> ChangeAsync(ModuleComputer computer, WallpaperRequest request, CancellationToken cancellation) =>
        AppearanceActionWorkflow.ChangeWallpaperAsync(new(!request.WindowsDefault, request.Desktop, request.SignIn), new Runtime(client, uploads, computer, request, cancellation));

    sealed class Runtime(IModuleClient client, IModuleUploads uploads, ModuleComputer computer, WallpaperRequest request, CancellationToken cancellation) : IWallpaperChangeRuntime
    {
        string _remote = "";

        public async Task UploadAsync()
        {
            var picture = request.Picture ?? throw new InvalidOperationException("No picture was chosen.");
            _remote = (await uploads.UploadAsync(computer, picture, ModuleUploads.Wallpaper, cancellation)).RemotePath;
        }

        public Task DesktopAsync() => request.WindowsDefault
            ? Call(WallpaperDefault, [])
            : Call(WallpaperSet, new JsonObject { ["path"] = _remote, ["style"] = request.Style, ["name"] = request.Picture!.Name });

        public Task SignInAsync() => request.WindowsDefault
            ? request.UserOnly ? Call(LockScreenReset, []) : ResetSignInAsync()
            : request.UserOnly
                ? Call(LockScreenSet, new JsonObject { ["path"] = _remote })
                : CallAsSystem(SignInSet, new JsonObject { ["path"] = _remote, ["sha256"] = request.Picture!.Sha256 });

        async Task ResetSignInAsync()
        {
            var answer = JsonNode.Parse(await client.CallAsSystemAsync(computer, SignInDefault, "{}", cancellation)) as JsonObject;
            if (answer?[HomeKey]?.GetValue<bool>() != true) return;
            if (!request.CanResetLockScreen)
                throw new InvalidOperationException("This is Windows Home, where each user has their own lock screen picture. Update the client to reset it from here.");
            if (!request.HasUser)
                throw new InvalidOperationException("This is Windows Home, where each user has their own lock screen picture, and nobody is signed in to reset it for.");
            await Call(LockScreenReset, []);
        }

        Task Call(string command, JsonObject arguments) => client.CallAsync(computer, command, arguments.ToJsonString(), cancellation);

        Task CallAsSystem(string command, JsonObject arguments) => client.CallAsSystemAsync(computer, command, arguments.ToJsonString(), cancellation);
    }
}
