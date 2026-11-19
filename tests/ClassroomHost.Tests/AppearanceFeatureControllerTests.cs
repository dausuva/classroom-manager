// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

// T-222 MW-A: controller can be composed without MainWindow or real settings/transport; denied/empty paths never open dialogs.
public sealed class AppearanceFeatureControllerTests
{
    static void OnDispatcher(Func<Task> test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); }
                catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        failure?.Throw();
    }

    [Fact]
    public void Every_entry_point_rejects_empty_selection_before_dialog_or_transport() => OnDispatcher(async () =>
    {
        var controller = new AppearanceFeatureController(new Window(), new(), () => [],
            (_, _, _) => throw new Exception("Unexpected support check"), (_, _, _, _, _, _) => throw new Exception("Unexpected operation"));
        await controller.WallpaperAsync();
        await controller.WallpaperResetAsync();
        await controller.ScreenSaverAsync();
        controller.ManagedWallpapers();
        controller.RecentWallpapers();
        controller.WindowsThemes();
        await controller.WindowsPreferencesAsync();
        await controller.MouseAsync();
        await controller.MouseResetAsync();
        await controller.DesktopLayoutResetAsync();
    });

    [Fact]
    public void Supported_feature_checks_still_precede_views_and_changes() => OnDispatcher(async () =>
    {
        var checks = new List<string>();
        var controller = new AppearanceFeatureController(new Window(), new(), () => [new() { Room = "fixture" }],
            (_, command, _) => { checks.Add(command); return false; }, (_, _, _, _, _, _) => throw new Exception("Unexpected operation"));
        await controller.ScreenSaverAsync();
        controller.ManagedWallpapers();
        controller.RecentWallpapers();
        controller.WindowsThemes();
        await controller.WindowsPreferencesAsync();
        await controller.DesktopLayoutResetAsync();
        Assert.Equal(6, checks.Count);
        Assert.Equal(6, checks.Distinct().Count());
    });

    [Fact]
    public void Async_entry_point_preserves_faulted_task_timing_for_selection_errors() => OnDispatcher(async () =>
    {
        var error = new InvalidOperationException("fixture");
        var controller = new AppearanceFeatureController(new Window(), new(), () => throw error, (_, _, _) => false,
            (_, _, _, _, _, _) => Task.CompletedTask);
        Task? task = null;
        Assert.Null(Record.Exception(() => { task = controller.ScreenSaverAsync(); }));
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => task!));
    });
}
