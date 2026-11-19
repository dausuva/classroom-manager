// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

// T-222 MW-B: denied/empty admission does not create a view or transport session.
public sealed class SoftwareDeploymentControllerTests
{
    [Fact]
    public async Task Runner_preserves_shell_default_limit_and_explicit_all_at_once_sentinel()
    {
        var batches = new List<int>();
        SoftwareDeploymentRunner runner = (_, _, _, batch, _) => { batches.Add(batch); return Task.CompletedTask; };
        await runner([], "fixture", (_, _) => Task.CompletedTask);
        await runner([], "fixture", (_, _) => Task.CompletedTask, maxParallel: 0);
        Assert.Equal(new[] { -1, 0 }, batches);
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Staged_empty_or_denied_selection_never_opens_dialogs(bool denied) => OnDispatcher(async () =>
    {
        var checks = 0;
        var controller = new SoftwareDeploymentController(new Window(), new(), () => denied ? [new()] : [],
            () => throw new Exception("Unexpected roster read"), () => "",
            (_, command, _) => { checks++; Assert.Equal(ClassroomManager.Shared.Cmd.StagingList, command); return false; },
            (_, _, _, _, _) => throw new Exception("Unexpected operation"), _ => throw new Exception("Unexpected log"));
        await controller.StagedUploadsAsync();
        Assert.Equal(denied ? 1 : 0, checks);
    });

    [Fact]
    public void Install_setup_failure_remains_a_faulted_task_not_a_synchronous_throw() => OnDispatcher(async () =>
    {
        var error = new InvalidOperationException("fixture");
        var controller = new SoftwareDeploymentController(new Window(), new(), () => [], () => throw error,
            () => "", (_, _, _) => false, (_, _, _, _, _) => Task.CompletedTask, _ => { });
        Task? task = null;
        Assert.Null(Record.Exception(() => { task = controller.InstallAsync([]); }));
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => task!));
    });
}
