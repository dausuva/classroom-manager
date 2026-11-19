// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ClassroomManager.Appearance;
using ClassroomManager.Host;
using ClassroomManager.Modules;
using Xunit;

namespace ClassroomHost.Tests;

// T-222 slice 2: the ports client.upload and storage.source. A module names a picture by a handle the Host issued; the Host decides what
// is sent, where it lands and whether the file is still the one the teacher picked.
public sealed class ModuleUploadPortTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("cm-upload-port-").FullName;
    readonly List<string> _audit = [];

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    sealed class Computer(string? staging = @"C:\staging") : IModuleConnection
    {
        public List<(string Command, bool System, string Arguments)> Calls { get; } = [];
        public List<(string Local, string Remote)> Uploads { get; } = [];
        public string? Home { get; set; }
        public Exception? UploadFails { get; set; }

        public string? StagingFolder => staging;

        public Task<string> CallUserAsync(string command, string argumentsJson, CancellationToken cancellation) { Calls.Add((command, false, argumentsJson)); return Task.FromResult("{}"); }

        public Task<string> CallSystemAsync(string command, string argumentsJson, CancellationToken cancellation)
        {
            Calls.Add((command, true, argumentsJson));
            return Task.FromResult(command == WallpaperOperations.SignInDefault ? $"{{\"home\":{(Home == "true" ? "true" : "false")}}}" : "{}");
        }

        public Task DownloadAsync(string remotePath, string localPath, CancellationToken cancellation) => throw new NotSupportedException();

        public Task UploadAsync(string localPath, string remotePath, CancellationToken cancellation)
        {
            if (UploadFails is not null) throw UploadFails;
            Uploads.Add((localPath, remotePath));
            return Task.CompletedTask;
        }
    }

    sealed class NoSinks : IModuleSinkFolders
    {
        public string? Resolve(string sink) => null;
    }

    (ModuleClientSession Run, ModuleComputer Computer, Computer Connection, PickedFiles Files) Open(ModuleDescriptor? descriptor = null, Computer? connection = null)
    {
        connection ??= new Computer();
        var computer = new ModuleComputer(1, "PC-01");
        var files = new PickedFiles();
        return (new ModuleClientSession(descriptor ?? new AppearanceInventoryModuleEntry().Describe(), [new(computer, connection, "PC-01")], new NoSinks(), _audit.Add, sources: files),
            computer, connection, files);
    }

    string File_(string name, byte[]? content = null)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content ?? [1, 2, 3, 4, 5]);
        return path;
    }

    [Fact]
    public async Task A_picked_picture_goes_to_the_staging_folder_under_a_name_the_kind_decides()
    {
        var (run, computer, connection, files) = Open();
        var picture = await files.PickPictureAsync(File_("Class Photo.PNG"));

        var result = await run.UploadAsync(computer, picture, ModuleUploads.Wallpaper);

        Assert.Equal(@"C:\staging\wallpaper.png", result.RemotePath);
        Assert.Equal([(Path.Combine(_folder, "Class Photo.PNG"), @"C:\staging\wallpaper.png")], connection.Uploads);
        Assert.DoesNotContain(_audit, line => line.Contains(_folder));   // the log says what was done, never where the teacher keeps files
    }

    [Fact]
    public async Task The_handle_does_not_show_the_path()
    {
        var (_, _, _, files) = Open();
        var picture = await files.PickPictureAsync(File_("a.jpg"));

        Assert.DoesNotContain(_folder, picture.ToString());
        Assert.Equal("a.jpg", picture.Name);
        Assert.Equal(5, picture.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3, 4, 5])).ToLowerInvariant(), picture.Sha256);
    }

    [Fact]
    public async Task A_handle_the_Host_did_not_issue_is_refused_even_when_it_looks_right()
    {
        var (run, computer, connection, files) = Open();
        var real = await files.PickPictureAsync(File_("a.png"));

        // A module that invents a handle, or edits the size and checksum of a real one, still names only what the Host issued.
        await Assert.ThrowsAsync<ModulePortException>(() => run.UploadAsync(computer, new("0123", "a.png", 5, real.Sha256), ModuleUploads.Wallpaper));
        await Assert.ThrowsAsync<ModulePortException>(() => run.UploadAsync(computer, new(@"C:\Windows\System32\config\SAM", "SAM.png", 5, real.Sha256), ModuleUploads.Wallpaper));
        Assert.Empty(connection.Uploads);
        Assert.Equal(2, run.Refusals);
    }

    [Fact]
    public async Task A_file_that_changed_after_it_was_picked_is_not_sent()
    {
        var (run, computer, connection, files) = Open();
        var path = File_("a.png");
        var picture = await files.PickPictureAsync(path);
        File.WriteAllBytes(path, [9, 9, 9, 9, 9]);

        var error = await Assert.ThrowsAsync<ModulePortException>(() => run.UploadAsync(computer, picture, ModuleUploads.Wallpaper));

        Assert.Contains("changed after it was picked", error.Message);
        Assert.Empty(connection.Uploads);
    }

    [Theory]
    [InlineData("a.exe")]
    [InlineData("a.scr")]
    [InlineData("a.png.exe")]
    [InlineData("a")]
    public async Task Only_picture_types_can_be_picked(string name)
    {
        var (_, _, _, files) = Open();

        await Assert.ThrowsAsync<InvalidOperationException>(() => files.PickPictureAsync(File_(name)));
    }

    [Fact]
    public async Task An_empty_or_oversized_picture_cannot_be_picked()
    {
        var (_, _, _, files) = Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.PickPictureAsync(File_("empty.png", [])));
        var big = Path.Combine(_folder, "big.png");
        await using (var stream = File.Create(big)) stream.SetLength(ModuleUploads.MaxImageBytes + 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => files.PickPictureAsync(big));

        Assert.Contains("32 MB", error.Message);
    }

    [Fact]
    public async Task A_kind_the_module_did_not_declare_is_refused()
    {
        var descriptor = new AppearanceInventoryModuleEntry().Describe() with { };
        var withoutUploads = new ModuleDescriptor(descriptor.Id, descriptor.Name, descriptor.ContractVersion, descriptor.ClientCapabilities) { Ports = descriptor.Ports };
        var (run, computer, connection, files) = Open(withoutUploads);
        var picture = await files.PickPictureAsync(File_("a.png"));

        await Assert.ThrowsAsync<ModulePortException>(() => run.UploadAsync(computer, picture, ModuleUploads.Wallpaper));
        await Assert.ThrowsAsync<ModulePortException>(() => run.UploadAsync(computer, picture, "screensaver"));
        Assert.Empty(connection.Uploads);
    }

    [Fact]
    public async Task A_module_that_did_not_ask_for_the_ports_is_not_given_them()
    {
        var module = new ModuleDescriptor("test.module.upload", "Test", ModuleContract.CurrentVersion, []) { Uploads = [ModuleUploads.Wallpaper], Ports = [new(ModulePorts.ClientCall, 1)] };
        var (run, computer, connection, files) = Open(module);
        var picture = await files.PickPictureAsync(File_("a.png"));

        var error = await Assert.ThrowsAsync<ModulePortException>(() => run.UploadAsync(computer, picture, ModuleUploads.Wallpaper));

        Assert.Contains("client.upload", error.Message);
        Assert.Empty(connection.Uploads);
    }

    [Fact]
    public async Task A_computer_that_was_not_chosen_for_the_run_gets_nothing()
    {
        var (run, _, connection, files) = Open();
        var picture = await files.PickPictureAsync(File_("a.png"));

        await Assert.ThrowsAsync<ModulePortException>(() => run.UploadAsync(new(99, "PC-99"), picture, ModuleUploads.Wallpaper));
        Assert.Empty(connection.Uploads);
    }

    [Fact]
    public async Task A_connection_with_no_staging_folder_cannot_take_an_upload()
    {
        var (run, computer, connection, files) = Open(connection: new Computer(staging: null));
        var picture = await files.PickPictureAsync(File_("a.png"));

        await Assert.ThrowsAsync<ModulePortException>(() => run.UploadAsync(computer, picture, ModuleUploads.Wallpaper));
        Assert.Empty(connection.Uploads);
    }

    [Fact]
    public async Task The_run_stops_after_three_refusals_and_after_twenty_uploads_no_more_are_taken()
    {
        var (run, computer, connection, files) = Open();
        var picture = await files.PickPictureAsync(File_("a.png"));
        for (var count = 0; count < ModulePorts.MaxUploadsPerRun; count++) await run.UploadAsync(computer, picture, ModuleUploads.Wallpaper);

        await Assert.ThrowsAsync<ModulePortException>(() => run.UploadAsync(computer, picture, ModuleUploads.Wallpaper));

        Assert.Equal(ModulePorts.MaxUploadsPerRun, connection.Uploads.Count);
    }

    [Fact]
    public void A_manifest_may_list_uploads_only_with_both_ports_and_only_kinds_the_Host_knows()
    {
        Assert.Empty(ModuleManifest.UploadProblems([ModuleUploads.Wallpaper], [new(ModulePorts.ClientUpload, 1), new(ModulePorts.StorageSource, 1)]));
        Assert.Contains(ModuleManifest.UploadProblems([ModuleUploads.Wallpaper], [new(ModulePorts.ClientUpload, 1)]), text => text.Contains("storage.source"));
        Assert.Contains(ModuleManifest.UploadProblems([ModuleUploads.Wallpaper], []), text => text.Contains("client.upload"));
        Assert.Contains(ModuleManifest.UploadProblems(["firmware"], [new(ModulePorts.ClientUpload, 1), new(ModulePorts.StorageSource, 1)]), text => text.Contains("firmware"));
        Assert.Contains(ModuleManifest.UploadProblems([ModuleUploads.Wallpaper, ModuleUploads.Wallpaper], [new(ModulePorts.ClientUpload, 1), new(ModulePorts.StorageSource, 1)]), text => text.Contains("twice"));
    }

    // The module's own sequencing, through the ports.
    static WallpaperRequest Request(bool desktop, bool signIn, bool windowsDefault = false, bool userOnly = false, ModulePickedFile? picture = null, bool canReset = true, bool hasUser = true) =>
        new(desktop, signIn, windowsDefault, userOnly, picture, "fill", canReset, hasUser);

    [Fact]
    public async Task The_module_sends_one_upload_then_the_desktop_as_the_user_and_the_sign_in_picture_with_administrator_rights()
    {
        var (run, computer, connection, files) = Open();
        var picture = await files.PickPictureAsync(File_("a.jpg"));

        var result = await new WallpaperOperations(run, run).ChangeAsync(computer, Request(true, true, picture: picture), CancellationToken.None);

        Assert.Null(result.DesktopError);
        Assert.Null(result.SignInError);
        Assert.Single(connection.Uploads);
        Assert.Equal([(WallpaperOperations.WallpaperSet, false), (WallpaperOperations.SignInSet, true)], connection.Calls.Select(call => (call.Command, call.System)));
        var signIn = JsonNode.Parse(connection.Calls[1].Arguments)!;
        Assert.Equal(@"C:\staging\wallpaper.jpg", signIn["path"]!.GetValue<string>());
        Assert.Equal(picture.Sha256, signIn["sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task One_place_can_fail_while_the_other_works_and_both_outcomes_are_reported()
    {
        var (run, computer, connection, files) = Open();
        var picture = await files.PickPictureAsync(File_("a.png"));
        var failing = new Computer();
        var (run2, computer2, _, files2) = Open(connection: failing);
        var picture2 = await files2.PickPictureAsync(File_("b.png"));
        failing.UploadFails = new IOException("disk full");

        await Assert.ThrowsAsync<IOException>(() => new WallpaperOperations(run2, run2).ChangeAsync(computer2, Request(true, true, picture: picture2), CancellationToken.None));
        Assert.Empty(failing.Calls);   // a failed upload changes nothing

        var ok = await new WallpaperOperations(run, run).ChangeAsync(computer, Request(true, false, picture: picture), CancellationToken.None);
        Assert.Null(ok.DesktopError);
        Assert.Equal([WallpaperOperations.WallpaperSet], connection.Calls.Select(call => call.Command));
    }

    [Fact]
    public async Task Putting_Windows_defaults_back_uploads_nothing()
    {
        var (run, computer, connection, _) = Open();

        await new WallpaperOperations(run, run).ChangeAsync(computer, Request(true, true, windowsDefault: true), CancellationToken.None);

        Assert.Empty(connection.Uploads);
        Assert.Equal([(WallpaperOperations.WallpaperDefault, false), (WallpaperOperations.SignInDefault, true)], connection.Calls.Select(call => (call.Command, call.System)));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task On_Windows_Home_the_users_own_lock_screen_is_reset_only_when_the_client_can_and_somebody_is_signed_in(bool canReset, bool hasUser, bool resets)
    {
        var connection = new Computer { Home = "true" };
        var (run, computer, _, _) = Open(connection: connection);

        var result = await new WallpaperOperations(run, run).ChangeAsync(computer, Request(false, true, windowsDefault: true, canReset: canReset, hasUser: hasUser), CancellationToken.None);

        Assert.Equal(resets, connection.Calls.Any(call => call.Command == WallpaperOperations.LockScreenReset));
        Assert.Equal(resets, result.SignInError is null);
    }

    [Fact]
    public async Task The_lock_screen_of_the_signed_in_user_is_set_as_the_user_never_with_administrator_rights()
    {
        var (run, computer, connection, files) = Open();
        var picture = await files.PickPictureAsync(File_("a.png"));

        await new WallpaperOperations(run, run).ChangeAsync(computer, Request(false, true, userOnly: true, picture: picture), CancellationToken.None);

        Assert.Equal([(WallpaperOperations.LockScreenSet, false)], connection.Calls.Select(call => (call.Command, call.System)));
    }
}
