// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ClientUpdatePlanTests
{
    static ClientInfo Pc(string name, string status = "Online", string architecture = "x64", string? runtime = "10.0.1", string exclusive = "") =>
        new() { Name = name, Status = status, Architecture = architecture, DesktopRuntime = runtime, ExclusiveOperation = exclusive };

    [Fact]
    public void An_update_is_in_progress_while_any_computer_holds_an_exclusive_operation()
    {
        Assert.False(ClientUpdatePlan.InProgress([Pc("a"), Pc("b")]));
        Assert.True(ClientUpdatePlan.InProgress([Pc("a"), Pc("b", exclusive: "A Classroom Client update is in progress.")]));
        Assert.False(ClientUpdatePlan.InProgress([]));
    }

    [Fact]
    public void Online_means_a_status_that_starts_with_Online()
    {
        var online = ClientUpdatePlan.Online([Pc("a", "Online"), Pc("b", "Online (old key)"), Pc("c", "Offline"), Pc("d", "Not online")]);
        Assert.Equal(["a", "b"], online.Select(c => c.Name));
    }

    [Fact]
    public void The_choices_say_how_many_computers_each_means_and_where()
    {
        Assert.Equal(
            ["Selected computers (2)", "Every computer in the list - all rooms (7)", "Only online computers - all rooms (5)"],
            ClientUpdatePlan.TargetChoices(2, 7, 5, ""));
        Assert.Equal(
            ["Selected computers (0)", "Every computer in the list - room Lab 1 (3)", "Only online computers - room Lab 1 (1)"],
            ClientUpdatePlan.TargetChoices(0, 3, 1, "Lab 1"));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(30, 0)]
    public void The_selection_is_offered_first_when_there_is_one_otherwise_the_online_computers(int picked, int expected) =>
        Assert.Equal(expected, ClientUpdatePlan.DefaultChoice(picked));

    [Fact]
    public void Each_choice_picks_its_own_list_and_an_unknown_choice_means_the_selection()
    {
        List<ClientInfo> picked = [Pc("p")], inView = [Pc("v1"), Pc("v2")], online = [Pc("o")];
        Assert.Same(picked, ClientUpdatePlan.Targets(0, picked, inView, online));
        Assert.Same(inView, ClientUpdatePlan.Targets(1, picked, inView, online));
        Assert.Same(online, ClientUpdatePlan.Targets(2, picked, inView, online));
        Assert.Same(picked, ClientUpdatePlan.Targets(-1, picked, inView, online));
        Assert.Same(picked, ClientUpdatePlan.Targets(3, picked, inView, online));
    }

    [Theory]
    [InlineData("8", 8)]
    [InlineData("1", 1)]
    [InlineData("", 0)]
    [InlineData("  ", 0)]
    [InlineData("0", 0)]
    [InlineData("-3", 0)]
    [InlineData("many", 0)]
    [InlineData("2.5", 0)]
    public void The_batch_is_a_positive_number_and_anything_else_means_all_at_once(string typed, int expected) =>
        Assert.Equal(expected, ClientUpdatePlan.Batch(typed));

    [Fact]
    public void A_build_that_carries_its_runtime_fits_every_computer_whatever_it_reported()
    {
        var targets = new List<ClientInfo> { Pc("a", runtime: null), Pc("b", runtime: "") };
        var result = ClientUpdatePlan.ScreenRuntime(targets, new ClientBuildRuntime(false, 10, ".NET 10", false));
        Assert.Equal(ClientUpdatePlan.Fit.All, result.Fit);
        Assert.Same(targets, result.Fitting);
        Assert.Equal("", result.Explanation);
    }

    [Fact]
    public void A_shared_runtime_build_goes_only_to_computers_that_reported_a_runtime_and_the_rest_are_named()
    {
        var build = new ClientBuildRuntime(true, 10, ".NET 10 Desktop Runtime", false);
        var ready = Pc("ready", runtime: "10.0.5");
        var newer = Pc("newer", runtime: "11.0.0");
        var older = Pc("older", runtime: "8.0.1");
        var none = Pc("none", runtime: "");
        var silent = Pc("silent", runtime: null);

        var all = ClientUpdatePlan.ScreenRuntime([ready, newer], build);
        Assert.Equal(ClientUpdatePlan.Fit.All, all.Fit);

        var some = ClientUpdatePlan.ScreenRuntime([ready, older, none, silent, newer], build);
        Assert.Equal(ClientUpdatePlan.Fit.Some, some.Fit);
        Assert.Equal(["ready", "newer"], some.Fitting.Select(c => c.Name));
        Assert.Contains("older", some.Explanation);
        Assert.Contains("none", some.Explanation);
        Assert.Contains("silent", some.Explanation);

        var nothing = ClientUpdatePlan.ScreenRuntime([older, silent], build);
        Assert.Equal(ClientUpdatePlan.Fit.None, nothing.Fit);
        Assert.Empty(nothing.Fitting);
        Assert.NotEqual("", nothing.Explanation);
    }

    [Fact]
    public void An_empty_target_list_has_nothing_to_screen_and_is_left_alone()
    {
        var build = new ClientBuildRuntime(true, 10, ".NET 10", false);
        var result = ClientUpdatePlan.ScreenRuntime([], build);
        Assert.Equal(ClientUpdatePlan.Fit.All, result.Fit);
        Assert.Empty(result.Fitting);
        Assert.Equal(ClientUpdatePlan.Fit.All, ClientUpdatePlan.ScreenArchitecture([], PeArchitecture.X64).Fit);
    }

    [Fact]
    public void A_build_goes_only_to_computers_of_its_own_processor_and_the_others_are_named()
    {
        var x64 = Pc("x64", architecture: "x64");
        var old = Pc("old", architecture: "");   // a Client from before the 32-bit build does not say, and is x64
        var x86 = Pc("x86", architecture: "x86");

        var all = ClientUpdatePlan.ScreenArchitecture([x64, old], PeArchitecture.X64);
        Assert.Equal(ClientUpdatePlan.Fit.All, all.Fit);
        Assert.Equal(2, all.Fitting.Count);

        var some = ClientUpdatePlan.ScreenArchitecture([x64, x86, old], PeArchitecture.X64);
        Assert.Equal(ClientUpdatePlan.Fit.Some, some.Fit);
        Assert.Equal(["x64", "old"], some.Fitting.Select(c => c.Name));
        Assert.Contains("x86", some.Explanation);
        Assert.Contains("Update only the 2 computer(s)", some.Explanation);

        var none = ClientUpdatePlan.ScreenArchitecture([x86], PeArchitecture.X64);
        Assert.Equal(ClientUpdatePlan.Fit.None, none.Fit);
        Assert.Empty(none.Fitting);
        Assert.Contains("nothing will be updated", none.Explanation);

        // A build whose processor cannot be read is left to the Client's own start-up check.
        Assert.Equal(ClientUpdatePlan.Fit.All, ClientUpdatePlan.ScreenArchitecture([x86, x64], PeArchitecture.Unknown).Fit);
    }

    [Fact]
    public void The_hash_is_the_lowercase_sha256_of_the_file_and_the_arguments_carry_it()
    {
        var file = Path.Combine(Path.GetTempPath(), "update-plan-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllText(file, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ClientUpdatePlan.Sha256Of(file));
            Assert.Equal($"Update client ({Path.GetFileName(file)})", ClientUpdatePlan.Title(file));
        }
        finally { File.Delete(file); }

        var arguments = ClientUpdatePlan.Arguments(@"C:\stage\update\ClassroomClient.exe", "ab12", force: true);
        Assert.Equal(@"C:\stage\update\ClassroomClient.exe", arguments["path"]!.GetValue<string>());
        Assert.Equal("ab12", arguments["sha256"]!.GetValue<string>());
        Assert.True(arguments["force"]!.GetValue<bool>());
        Assert.False(ClientUpdatePlan.Arguments("p", "h", force: false)["force"]!.GetValue<bool>());
    }
}
