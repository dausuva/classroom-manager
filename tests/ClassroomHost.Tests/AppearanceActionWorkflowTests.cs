// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Appearance;
using Xunit;

namespace ClassroomHost.Tests;

// T-222 MW-A: independent module owns ordering, not transport, authorization, UI or rollback storage.
public sealed class AppearanceActionWorkflowTests
{
    sealed class Runtime : IWallpaperChangeRuntime, IScreenSaverChangeRuntime, IWindowsPreferencesRuntime
    {
        public List<string> Calls = [];
        public HashSet<string> Fail = [];
        public Func<string, Task>? Work;
        Task Call(string name)
        {
            Calls.Add(name);
            if (Fail.Contains(name)) throw new IOException(name);
            return Work?.Invoke(name) ?? Task.CompletedTask;
        }
        public Task UploadAsync() => Call("upload");
        public Task DesktopAsync() => Call("desktop");
        public Task SignInAsync() => Call("sign-in");
        public Task SetAsync() => Call("set-saver");
        public Task RestoreAsync() => Call("restore-saver");
        public Task RestoreDisplayAsync() => Call("restore-display");
        public Task SetDisplayAsync(int seconds) => Call("display:" + seconds);
        public Task InstallLanguageAsync() => Call("install-language");
        public Task ApplyAsync() => Call("preferences");
    }

    [Theory]
    [InlineData(false, true, false, "desktop")]
    [InlineData(false, false, true, "sign-in")]
    [InlineData(false, true, true, "desktop,sign-in")]
    [InlineData(true, true, false, "upload,desktop")]
    [InlineData(true, false, true, "upload,sign-in")]
    [InlineData(true, true, true, "upload,desktop,sign-in")]
    public async Task Wallpaper_attempts_only_selected_destinations_after_upload(bool upload, bool desktop, bool signIn, string calls)
    {
        var runtime = new Runtime();
        var result = await AppearanceActionWorkflow.ChangeWallpaperAsync(new(upload, desktop, signIn), runtime);
        Assert.Equal(calls.Split(','), runtime.Calls);
        Assert.Null(result.DesktopError);
        Assert.Null(result.SignInError);
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("sign-in")]
    [InlineData("both")]
    public async Task Independent_wallpaper_destinations_keep_partial_failures_and_try_the_other_place(string failed)
    {
        var runtime = new Runtime();
        if (failed is "desktop" or "both") runtime.Fail.Add("desktop");
        if (failed is "sign-in" or "both") runtime.Fail.Add("sign-in");
        var result = await AppearanceActionWorkflow.ChangeWallpaperAsync(new(false, true, true), runtime);
        Assert.Equal(new[] { "desktop", "sign-in" }, runtime.Calls);
        Assert.Equal(failed is "desktop" or "both" ? "desktop" : null, result.DesktopError);
        Assert.Equal(failed is "sign-in" or "both" ? "sign-in" : null, result.SignInError);
    }

    [Fact]
    public async Task Failed_upload_cannot_apply_either_wallpaper_destination()
    {
        var runtime = new Runtime { Fail = ["upload"] };
        await Assert.ThrowsAsync<IOException>(() => AppearanceActionWorkflow.ChangeWallpaperAsync(new(true, true, true), runtime));
        Assert.Equal(new[] { "upload" }, runtime.Calls);
    }

    [Theory]
    [InlineData(false, false, null, false, "set-saver")]
    [InlineData(false, true, 900, false, "upload,set-saver,display:900")]
    [InlineData(false, false, 0, false, "set-saver,display:0")]
    [InlineData(true, false, null, true, "restore-display,restore-saver")]
    [InlineData(true, true, 900, false, "restore-saver")]
    public async Task Saver_and_display_order_includes_restore_and_zero_without_fabricated_rollback(bool restore, bool upload, int? seconds, bool supported, string calls)
    {
        var runtime = new Runtime();
        await AppearanceActionWorkflow.ChangeScreenSaverAsync(new(restore, upload, seconds, supported), runtime);
        Assert.Equal(calls.Split(','), runtime.Calls);
    }

    [Theory]
    [InlineData("upload", false, "upload")]
    [InlineData("set-saver", false, "upload,set-saver")]
    [InlineData("display:900", false, "upload,set-saver,display:900")]
    [InlineData("restore-display", true, "restore-display")]
    [InlineData("restore-saver", true, "restore-display,restore-saver")]
    public async Task Saver_failure_stops_at_the_existing_step_without_replaying_mutations(string failed, bool restore, string calls)
    {
        var runtime = new Runtime { Fail = [failed] };
        await Assert.ThrowsAsync<IOException>(() => AppearanceActionWorkflow.ChangeScreenSaverAsync(new(restore, true, 900, true), runtime));
        Assert.Equal(calls.Split(','), runtime.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Language_install_precedes_preferences_only_when_requested(bool install)
    {
        var runtime = new Runtime();
        await AppearanceActionWorkflow.ChangePreferencesAsync(install, runtime);
        Assert.Equal(install ? new[] { "install-language", "preferences" } : new[] { "preferences" }, runtime.Calls);
    }

    [Fact]
    public async Task Language_install_failure_cannot_apply_preferences()
    {
        var runtime = new Runtime { Fail = ["install-language"] };
        await Assert.ThrowsAsync<IOException>(() => AppearanceActionWorkflow.ChangePreferencesAsync(true, runtime));
        Assert.Equal(new[] { "install-language" }, runtime.Calls);
    }

    [Fact]
    public async Task Upload_is_awaited_before_any_destination_starts()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime { Work = name => name == "upload" ? release.Task : Task.CompletedTask };
        var task = AppearanceActionWorkflow.ChangeWallpaperAsync(new(true, true, true), runtime);
        Assert.Equal(new[] { "upload" }, runtime.Calls);
        release.SetResult();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "upload", "desktop", "sign-in" }, runtime.Calls);
    }
}
