// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Appearance;
using ClassroomManager.Modules;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed record WallpaperChangeRequest(WallpaperTarget Target, bool WindowsDefault, bool UserOnly,
    string File = "", string Style = "", string Sha256 = "");
public sealed record ScreenSaverChangeRequest(bool Restore, bool Off, string Name, int Seconds, bool Secure,
    ScreenSaverFile? File, int? DisplaySeconds);

/// <summary>Non-owning core command adapters. BulkRunner/session admission, authorization and lifetime stay with the Host.</summary>
public sealed class ClientAppearanceActions(ClientInfo client, ClientSession session, string staging)
{
    public async Task ChangeWallpaperAsync(WallpaperChangeRequest request)
    {
        // The picture goes through the module's ports: the module holds a handle to the file, the Host keeps its path.
        var files = new PickedFiles();
        var picture = request.WindowsDefault ? null : await files.PickPictureAsync(request.File);
        var run = ModuleRuns.Open(BuiltInModules.Appearance, [(session, client)], _ => null, stagingFolder: staging, pickedFiles: files);
        var result = await new WallpaperOperations(run, run).ChangeAsync(run.Computers[0], new(
            request.Target.IncludesDesktop(), request.Target.IncludesSignIn(), request.WindowsDefault, request.UserOnly, picture, request.Style,
            client.SupportsCommand(Cmd.LockScreenReset), client.User.Length > 0), CancellationToken.None);
        var failure = WallpaperTargets.FailureMessage(request.Target, result.DesktopError, result.SignInError);
        if (failure.Length > 0) throw new InvalidOperationException(failure);
    }

    public async Task ChangeScreenSaverAsync(ScreenSaverChangeRequest request) => await AppearanceActionWorkflow.ChangeScreenSaverAsync(
        new(request.Restore, request.File is not null, request.DisplaySeconds, client.SupportsCommand(Cmd.PowerDisplaySet)), new SaverRuntime(this, request));

    sealed class SaverRuntime(ClientAppearanceActions owner, ScreenSaverChangeRequest request) : IScreenSaverChangeRuntime
    {
        string Remote => $@"{owner.Staging}\screensaver\{request.File!.Name}";
        public async Task UploadAsync()
        {
            owner.Client.Status = $"Uploading {request.File!.Name}...";
            await owner.Session.UploadAsync(request.File.Path, Remote);
        }
        public Task SetAsync()
        {
            var args = new JsonObject { ["mode"] = request.Off ? "none" : request.File is not null ? "staged" : "installed" };
            if (!request.Off)
            {
                args["name"] = request.Name;
                args["timeoutSec"] = request.Seconds;
                args["secure"] = request.Secure;
            }
            if (request.File is not null) { args["path"] = Remote; args["sha256"] = request.File.Sha256; }
            return owner.Session.CallAtAsync(Levels.User, Cmd.ScreenSaverSet, args);
        }
        public Task RestoreAsync() => owner.Session.CallAtAsync(Levels.User, Cmd.ScreenSaverReset);
        public Task RestoreDisplayAsync() => owner.Session.CallAsync(Cmd.PowerDisplaySet, new JsonObject { [DisplayPowerRules.Restore] = true });
        public Task SetDisplayAsync(int seconds) => owner.Session.CallAsync(Cmd.PowerDisplaySet, new JsonObject { [DisplayPowerRules.Seconds] = seconds });
    }

    public async Task ChangePreferencesAsync(string language, bool install, JsonObject arguments) =>
        await AppearanceActionWorkflow.ChangePreferencesAsync(install && language.Length > 0, new PreferencesRuntime(session, language, arguments));

    sealed class PreferencesRuntime(ClientSession session, string language, JsonObject arguments) : IWindowsPreferencesRuntime
    {
        public Task InstallLanguageAsync() => session.CallAsync(Cmd.LanguageInstall, new JsonObject { ["language"] = language });
        public Task ApplyAsync() => session.CallAtAsync(Levels.User, Cmd.WindowsPreferencesSet, arguments);
    }

    ClientInfo Client => client;
    ClientSession Session => session;
    string Staging => staging;
}
