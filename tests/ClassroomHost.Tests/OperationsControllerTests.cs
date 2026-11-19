// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>MW-E: what a run does to the Operations list, the computers and the log, with a stand-in for the connecting (<see cref="BulkRunner"/> has its own tests).</summary>
public sealed class OperationsControllerTests
{
    static ClientInfo Client(string name) => new() { Address = "10.0.0.1", Name = name };

    /// <summary>An executor that, for every target, plays Starting, then Succeeded or Failed (by name), then Finished, and notes the limit it was given.</summary>
    sealed class Script
    {
        public int Limit = -2;
        public readonly List<string> Ran = [];
        public readonly HashSet<string> Failing = [];
        public readonly HashSet<string> Cancelling = [];
        public int BusyWhileRunning;

        public Task Run(IReadOnlyList<ClientInfo> targets, Func<ClientInfo, ClientSession, Task> action, int limit, CancellationToken cancellation,
            BulkRunCallbacks callbacks, bool exclusive)
        {
            Limit = limit;
            foreach (var client in targets)
            {
                if (Cancelling.Contains(client.Name)) { callbacks.Cancelled?.Invoke(client); continue; }
                Ran.Add(client.Name);
                callbacks.Starting?.Invoke(client);
                BusyWhileRunning = Math.Max(BusyWhileRunning, client.BusyCount);
                if (Failing.Contains(client.Name)) callbacks.Failed?.Invoke(client, new InvalidOperationException("no luck"));
                else callbacks.Succeeded?.Invoke(client);
                callbacks.Finished?.Invoke(client);
            }
            return Task.CompletedTask;
        }
    }

    static (OperationsController Controller, Script Script, List<string> Log) Make()
    {
        var script = new Script();
        var log = new List<string>();
        var controller = new OperationsController(new HostSettings(), (message, _) => log.Add(message), script.Run);
        return (controller, script, log);
    }

    static Task<int> Nothing(ClientInfo _, ClientSession __) => Task.FromResult(0);

    [Fact]
    public async Task A_run_is_listed_selected_and_records_every_computer_with_its_status_and_log_lines()
    {
        var (controller, script, log) = Make();
        script.Failing.Add("PC-2");
        OperationActivity? added = null;
        controller.Added += operation => added = operation;
        var one = Client("PC-1");
        var two = Client("PC-2");

        await controller.RunAsync([one, two], "Lock screen", (c, s) => Nothing(c, s));

        var operation = Assert.Single(controller.Operations);
        Assert.Same(operation, added);
        Assert.Equal(1, operation.Succeeded);
        Assert.Equal(1, operation.Failed);
        Assert.True(operation.IsFinished);
        Assert.Equal(0, one.BusyCount);
        Assert.Equal(0, two.BusyCount);
        Assert.StartsWith("Error: ", two.Status);
        Assert.Equal(1, script.BusyWhileRunning);
        Assert.Contains("Lock screen: 2 computer(s), 2 at a time...", log.Single(line => line.StartsWith("Lock screen: 2")).Replace("all at once", "2 at a time"));
        Assert.Contains("  OK    PC-1", log);
        Assert.Contains(log, line => line.StartsWith("  FAIL  PC-2: "));
        Assert.Equal("Lock screen: done.", log[^1]);
    }

    [Theory]
    [InlineData(-1, 3, OperationsController.DefaultParallel)]
    [InlineData(0, 3, 3)]
    [InlineData(0, 0, 1)]
    [InlineData(2, 5, 2)]
    public async Task How_many_computers_run_at_once_follows_the_caller_and_the_default(int asked, int count, int expected)
    {
        var (controller, script, _) = Make();

        await controller.RunAsync(Enumerable.Range(0, count).Select(i => Client("PC-" + i)).ToList(), "x", (c, s) => Nothing(c, s), maxParallel: asked);

        Assert.Equal(expected, script.Limit);
    }

    [Fact]
    public async Task A_quiet_run_adds_nothing_to_the_list_or_the_log_and_marks_a_failing_computer_offline()
    {
        var (controller, script, log) = Make();
        script.Failing.Add("PC-1");
        var client = Client("PC-1");

        await controller.RunAsync([client], "Check", (c, s) => Nothing(c, s), quiet: true);

        Assert.Empty(controller.Operations);
        Assert.Empty(log);
        Assert.Equal("Offline", client.Status);
        Assert.Equal(0, client.BusyCount);
    }

    [Fact]
    public async Task A_computer_the_run_never_reached_is_recorded_as_cancelled_and_told_to_the_caller()
    {
        var (controller, script, _) = Make();
        script.Cancelling.Add("PC-2");
        var told = new List<(string, string)>();

        await controller.RunAsync([Client("PC-1"), Client("PC-2")], "x", (c, s) => Nothing(c, s), targetFinished: (c, text) => told.Add((c.Name, text)));

        Assert.Contains(("PC-2", "Cancelled before this computer started"), told);
        Assert.Contains(("PC-1", ""), told);
    }

    [Fact]
    public async Task Retry_is_offered_only_when_asked_for_and_runs_only_the_computers_that_failed()
    {
        var (controller, script, _) = Make();
        script.Failing.Add("PC-2");

        await controller.RunAsync([Client("PC-1"), Client("PC-2")], "Send", (c, s) => Nothing(c, s), allowRetry: true);
        var first = controller.Operations.Single();
        Assert.True(first.CanRetry);

        script.Ran.Clear();
        script.Failing.Clear();
        await first.RetryFailedAsync();

        Assert.Equal(["PC-2"], script.Ran);
        Assert.Equal(2, controller.Operations.Count);
        Assert.Equal("Send — retry failed", controller.Operations[0].Title);

        var plain = Make();
        plain.Script.Failing.Add("PC-1");
        await plain.Controller.RunAsync([Client("PC-1")], "x", (c, s) => Nothing(c, s));
        Assert.False(plain.Controller.Operations.Single().CanRetry);
    }

    [Fact]
    public async Task Undo_is_offered_for_the_computers_that_succeeded_and_runs_the_counterpart_on_them()
    {
        var (controller, script, _) = Make();
        script.Failing.Add("PC-2");

        await controller.RunAsync([Client("PC-1"), Client("PC-2")], "Lock", (c, s) => Nothing(c, s), undo: ("Unlock", (c, s) => Nothing(c, s)));
        var operation = controller.Operations.Single();
        Assert.True(operation.CanUndo);
        Assert.Equal("Unlock", operation.UndoLabel);

        script.Ran.Clear();
        await operation.UndoAsync();

        Assert.Equal(["PC-1"], script.Ran);
        Assert.Equal("Unlock", controller.Operations[0].Title);
    }

    [Fact]
    public async Task The_list_keeps_the_newest_hundred_and_drops_the_oldest_finished_ones_first()
    {
        var (controller, _, _) = Make();
        for (var i = 0; i < OperationsController.Kept + 5; i++)
            await controller.RunAsync([Client("PC")], "Run " + i, (c, s) => Nothing(c, s));

        Assert.Equal(OperationsController.Kept, controller.Operations.Count);
        Assert.Equal("Run " + (OperationsController.Kept + 4), controller.Operations[0].Title);
        Assert.Equal("Run 5", controller.Operations[^1].Title);
    }

    [Fact]
    public async Task Clearing_takes_out_the_finished_operations_and_counts_those_still_running()
    {
        var (controller, _, _) = Make();
        await controller.RunAsync([Client("PC")], "Done one", (c, s) => Nothing(c, s));
        var running = new OperationActivity("Still going", "1 computer", 1);
        running.Start();
        controller.Operations.Add(running);

        Assert.Equal(1, controller.Running);
        Assert.Equal(1, controller.ClearFinished());

        Assert.Same(running, controller.Operations.Single());
    }
}
