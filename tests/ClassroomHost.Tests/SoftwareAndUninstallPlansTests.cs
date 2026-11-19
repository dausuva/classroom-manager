// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>The decisions of the Install software dialogs and of Uninstall client (T-253 phase 2), without a window, and the wait until a removed client stops answering without real waiting.</summary>
public sealed class SoftwareAndUninstallPlansTests
{
    static ClientInfo Client(string name, string status = "Online") => new() { Name = name, Status = status };

    // ---- Install on: who -------------------------------------------------------------------

    [Fact]
    public void The_three_answers_to_Install_on_say_how_many_computers_each_means_and_in_which_room()
    {
        Assert.Equal(["Selected computers (2)", "Every computer in the list - room Lab 1 (30)", "Only online computers - room Lab 1 (28)"],
            SoftwareInstallPlan.TargetChoices(2, 30, 28, "Lab 1"));
        Assert.Equal("Every computer in the list - all rooms (5)", SoftwareInstallPlan.TargetChoices(0, 5, 4, "")[1]);
        Assert.Equal("all rooms", SoftwareInstallPlan.RoomText(""));
        Assert.Equal("room B2", SoftwareInstallPlan.RoomText("B2"));
    }

    [Fact]
    public void Selected_is_offered_first_only_when_some_computers_are_highlighted_and_each_answer_picks_its_list()
    {
        Assert.Equal(0, SoftwareInstallPlan.DefaultTargetIndex(1));
        Assert.Equal(2, SoftwareInstallPlan.DefaultTargetIndex(0));
        var picked = new List<ClientInfo> { Client("A") };
        var inView = new List<ClientInfo> { Client("A"), Client("B", "Offline") };
        var online = SoftwareInstallPlan.Online(inView);
        Assert.Equal(["A"], online.Select(client => client.Name));
        Assert.Same(picked, SoftwareInstallPlan.Targets(0, picked, inView, online));
        Assert.Same(inView, SoftwareInstallPlan.Targets(1, picked, inView, online));
        Assert.Same(online, SoftwareInstallPlan.Targets(2, picked, inView, online));
        Assert.Same(picked, SoftwareInstallPlan.Targets(7, picked, inView, online));
        Assert.Contains("Install on", SoftwareInstallPlan.NoComputers);
    }

    [Theory]
    [InlineData(SoftwareAction.WingetInstall, "Mozilla.Firefox", "", "", "Install Mozilla.Firefox")]
    [InlineData(SoftwareAction.WingetUninstall, "Mozilla.Firefox", "", "", "Uninstall Mozilla.Firefox")]
    [InlineData(SoftwareAction.Registry, "", @"C:\Kits\tweak.reg", "", "Apply registry file tweak.reg")]
    [InlineData(SoftwareAction.Exe, "", @"C:\Kits\setup.exe", "", "Install setup.exe")]
    [InlineData(SoftwareAction.Msi, "", @"C:\Kits\app.msi", @"C:\Staging\app-1.2.msi", "Install app-1.2.msi")]
    public void The_operation_is_named_after_the_package_the_registry_file_or_the_installer(SoftwareAction action, string id, string local, string remote, string expected) =>
        Assert.Equal(expected, SoftwareInstallPlan.Title(action, id, local, remote));

    // ---- an installer already uploaded ------------------------------------------------------

    [Fact]
    public void An_MSI_is_run_quietly_by_default_and_an_EXE_is_given_nothing()
    {
        Assert.True(SoftwareInstallPlan.IsMsi(@"sub\Setup.MSI"));
        Assert.False(SoftwareInstallPlan.IsMsi("setup.exe"));
        Assert.Equal("/qn /norestart", SoftwareInstallPlan.DefaultArguments(true));
        Assert.Equal("", SoftwareInstallPlan.DefaultArguments(false));
    }

    [Fact]
    public void The_choices_of_an_uploaded_EXE_become_a_level_a_timeout_and_a_review()
    {
        var choices = SoftwareInstallPlan.Staged("setup.exe", SoftwareInstallPlan.StandardUser, "  /S  ", "600", deleteAfter: true);
        Assert.Equal((Levels.User, "/S", 600, "600", true, ""), (choices.Level, choices.Arguments, choices.TimeoutSeconds, choices.TimeoutText, choices.DeleteAfter, choices.Warning));
        Assert.Equal("Install", choices.ReviewLabel);
        Assert.Equal(
        [
            ("Installer", "setup.exe"), ("Run as", SoftwareInstallPlan.StandardUser), ("Parameters", "/S"), ("Timeout", "600 seconds"), ("After success", "Delete the staged installer"),
        ], choices.ReviewItems.Select(item => (item.Label, item.Value)));
    }

    [Theory]
    [InlineData("29", 1800)]
    [InlineData("30", 30)]
    [InlineData("86400", 86400)]
    [InlineData("86401", 1800)]
    [InlineData("soon", 1800)]
    [InlineData("", 1800)]
    public void The_timeout_is_between_thirty_seconds_and_a_day_and_otherwise_half_an_hour(string typed, int used)
    {
        var choices = SoftwareInstallPlan.Staged("setup.msi", SoftwareInstallPlan.Elevated, "/qn", typed, false);
        Assert.Equal(used, choices.TimeoutSeconds);
        Assert.Equal(used.ToString(), choices.TimeoutText);
    }

    [Fact]
    public void An_EXE_run_as_SYSTEM_with_no_silent_parameters_is_warned_and_the_button_says_so_and_nothing_else_is()
    {
        var warned = SoftwareInstallPlan.Staged("setup.exe", SoftwareInstallPlan.Elevated, "  ", "1800", false);
        Assert.Contains("No silent parameters", warned.Warning);
        Assert.Equal("Install anyway", warned.ReviewLabel);
        Assert.Equal(Levels.System, warned.Level);
        Assert.Equal("(none)", warned.ReviewItems[2].Value);
        Assert.Equal("Keep the staged installer", warned.ReviewItems[4].Value);

        Assert.Equal("", SoftwareInstallPlan.Staged("setup.exe", SoftwareInstallPlan.Elevated, "/S", "1800", false).Warning);
        Assert.Equal("", SoftwareInstallPlan.Staged("setup.msi", SoftwareInstallPlan.Elevated, "", "1800", false).Warning);
        Assert.Equal("", SoftwareInstallPlan.Staged("setup.exe", SoftwareInstallPlan.StandardUser, "", "1800", false).Warning);
    }

    // ---- Uninstall client -------------------------------------------------------------------

    [Fact]
    public void The_removal_is_confirmed_by_the_computers_name_in_any_case_and_a_wrong_name_says_nothing_was_removed()
    {
        Assert.True(ClientUninstallPlan.NameMatches("pc-04", "PC-04"));
        Assert.False(ClientUninstallPlan.NameMatches("pc-05", "PC-04"));
        Assert.False(ClientUninstallPlan.NameMatches("", "PC-04"));
        Assert.Equal("The name does not match PC-04, so nothing was removed.", ClientUninstallPlan.NameMismatch("PC-04"));
        Assert.Contains("one computer at a time", ClientUninstallPlan.OnlyOne);
    }

    [Fact]
    public void The_request_carries_the_two_choices_under_the_names_the_Client_reads()
    {
        var args = ClientUninstallPlan.Arguments(keepData: true, removeLabTrust: false);
        Assert.Equal(["keepData", "removeLabTrust"], args.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.True((bool?)args["keepData"]);
        Assert.False((bool?)args["removeLabTrust"]);
    }

    [Fact]
    public void What_the_computer_answers_is_read_with_an_empty_text_counting_as_nothing()
    {
        var withTrust = ClientUninstallPlan.ReadAnswer(new JsonObject { ["log"] = @"C:\Windows\Temp\x.log", ["labTrust"] = "AB12", ["labTrustNote"] = "ignored" });
        Assert.Equal((@"C:\Windows\Temp\x.log", "AB12", "ignored"), (withTrust.LogPath, withTrust.LabTrustFingerprint, withTrust.LabTrustNote));
        var note = ClientUninstallPlan.ReadAnswer(new JsonObject { ["log"] = "l", ["labTrust"] = "", ["labTrustNote"] = "another program still needs it" });
        Assert.Null(note.LabTrustFingerprint);
        Assert.Equal("another program still needs it", note.LabTrustNote);
        var nothing = ClientUninstallPlan.ReadAnswer(new JsonObject());
        Assert.Equal((null, null, null), (nothing.LogPath, nothing.LabTrustFingerprint, nothing.LabTrustNote));
    }

    // ---- waiting until it is gone -----------------------------------------------------------

    sealed class FakeClock
    {
        public DateTime Now { get; private set; } = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        public int Delays { get; private set; }
        public Task Delay(TimeSpan span) { Now += span; Delays++; return Task.CompletedTask; }
    }

    static Func<Task<bool>> Answers(params bool[] sequence)
    {
        var index = 0;
        return () => Task.FromResult(sequence[Math.Min(index++, sequence.Length - 1)]);
    }

    [Fact]
    public async Task Three_silent_answers_in_a_row_mean_the_client_is_gone()
    {
        var clock = new FakeClock();
        Assert.True(await ClientUninstallPlan.WaitUntilGoneAsync(Answers(true, false, false, false), TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(3), 3, clock.Delay, () => clock.Now));
        Assert.Equal(4, clock.Delays);
    }

    [Fact]
    public async Task An_answer_in_between_starts_the_count_again()
    {
        var clock = new FakeClock();
        Assert.True(await ClientUninstallPlan.WaitUntilGoneAsync(Answers(false, false, true, false, false, false), TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(3), 3, clock.Delay, () => clock.Now));
        Assert.Equal(6, clock.Delays);
    }

    [Fact]
    public async Task A_client_that_keeps_answering_is_reported_after_the_time_is_up()
    {
        var clock = new FakeClock();
        Assert.False(await ClientUninstallPlan.WaitUntilGoneAsync(Answers(true), TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(3), 3, clock.Delay, () => clock.Now));
        Assert.Equal(60, clock.Delays);   // every three seconds for three minutes
        Assert.Equal(TimeSpan.FromMinutes(3), ClientUninstallPlan.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(3), ClientUninstallPlan.Interval);
        Assert.Equal(3, ClientUninstallPlan.SilentAnswersNeeded);
    }

    [Fact]
    public async Task A_listening_port_answers_and_a_closed_one_does_not()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try { Assert.True(await ClientUninstallPlan.AnswersAsync("127.0.0.1", port)); }
        finally { listener.Stop(); }
        Assert.False(await ClientUninstallPlan.AnswersAsync("127.0.0.1", port));
    }
}
