// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Modules;
using Xunit;

namespace ClassroomHost.Tests;

// A module whose run keeps asking for what it may not have is switched off by the broker, until the person ticks it again.
[Collection("ModuleBreaker")]
public sealed class ModuleBreakerTests : IDisposable
{
    const string Id = BuiltInModules.Software;

    public ModuleBreakerTests() => ModuleBreaker.Clear(Id);

    public void Dispose() => ModuleBreaker.Clear(Id);

    sealed class Quiet : IModuleConnection
    {
        public Task<string> CallUserAsync(string command, string argumentsJson, CancellationToken cancellation) => Task.FromResult("{}");
        public Task DownloadAsync(string remotePath, string localPath, CancellationToken cancellation) => Task.CompletedTask;
    }

    sealed class NoSinks : IModuleSinkFolders
    {
        public string? Resolve(string sink) => null;
    }

    static ModuleClientSession Run(Action<string> stopped)
    {
        var descriptor = BuiltInModules.Registry.Find(Id)!.Descriptor!;
        var computer = new ModuleComputer(1, "PC-01");
        return new ModuleClientSession(descriptor, [new(computer, new Quiet(), "PC-01")], new NoSinks(), _ => { }, stopped: stopped);
    }

    [Fact]
    public async Task The_third_refused_call_of_a_run_reports_the_stop_once()
    {
        var told = new List<string>();
        var run = Run(told.Add);
        var computer = run.Computers[0];

        for (var attempt = 0; attempt < 3; attempt++)
            await Assert.ThrowsAsync<ModulePortException>(() => run.CallAsync(computer, "power.shutdown", "{}"));
        await Assert.ThrowsAsync<ModulePortException>(() => run.CallAsync(computer, "power.shutdown", "{}"));   // the run is stopped; this is not refused again

        Assert.Single(told);
        Assert.Contains("3 refused calls", told[0]);
        Assert.True(run.Stopped);
    }

    [Fact]
    public async Task Two_refusals_do_not_stop_a_run()
    {
        var told = new List<string>();
        var run = Run(told.Add);
        for (var attempt = 0; attempt < 2; attempt++)
            await Assert.ThrowsAsync<ModulePortException>(() => run.CallAsync(run.Computers[0], "power.shutdown", "{}"));

        Assert.Empty(told);
        Assert.False(run.Stopped);
    }

    [Fact]
    public void A_stopped_module_is_off_for_the_Host_and_cannot_open_a_new_run()
    {
        var settings = new HostSettings { DisabledModules = [] };
        Assert.True(BuiltInModules.Enabled(settings, Id));

        ModuleBreaker.Trip(Id, "The run was stopped.");

        Assert.False(BuiltInModules.Enabled(settings, Id));
        Assert.Empty(settings.DisabledModules);   // it is not the person's switch, so nothing was saved
        Assert.Throws<InvalidOperationException>(() => ModuleRuns.Open(Id, [], _ => null));
    }

    [Fact]
    public void Clearing_the_stop_turns_the_module_back_on()
    {
        ModuleBreaker.Trip(Id, "x");
        ModuleBreaker.Clear(Id);

        Assert.True(BuiltInModules.Enabled(new HostSettings { DisabledModules = [] }, Id));
        Assert.Null(ModuleBreaker.Reason(Id));
    }

    [Fact]
    public void A_stop_is_remembered_with_its_first_reason()
    {
        ModuleBreaker.Trip(Id, "first");
        ModuleBreaker.Trip(Id, "second");

        Assert.Equal("first", ModuleBreaker.Reason(Id));
    }
}
