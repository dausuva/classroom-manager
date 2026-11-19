// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public class ProtocolTests
{
    [Fact]
    public void Current_version_matches_its_numeric_compatibility_components()
    {
        Assert.True(Protocol.TryParseVersion(Protocol.Version, out var major, out var minor));
        Assert.Equal(Protocol.Major, major);
        Assert.Equal(Protocol.Minor, minor);
    }

    [Theory]
    [InlineData(Cmd.WallpaperSet, true)]
    [InlineData(Cmd.WallpaperReset, true)]
    [InlineData(Cmd.WallpaperDefault, true)]
    [InlineData(Cmd.WallpaperColor, true)]
    [InlineData(Cmd.WallpaperManagedList, true)]
    [InlineData(Cmd.WallpaperManagedSet, true)]
    [InlineData(Cmd.WallpaperManagedDelete, true)]
    [InlineData(Cmd.WallpaperManagedPreview, true)]
    [InlineData(Cmd.WindowsPreferencesSet, true)]
    [InlineData(Cmd.WindowsPreferencesGet, true)]
    [InlineData(Cmd.LanguageInstall, false)]
    [InlineData(Cmd.WindowsThemesList, true)]
    [InlineData(Cmd.WindowsThemeSet, true)]
    [InlineData(Cmd.WindowsUpdateScan, false)]
    [InlineData(Cmd.WindowsUpdateInstall, false)]
    [InlineData(Cmd.WindowsUpdatePause, false)]
    [InlineData(Cmd.WindowsUpdateResume, false)]
    [InlineData(Cmd.WindowsUpdateStatus, false)]
    [InlineData(Cmd.WindowsUpdateRestart, false)]
    [InlineData(Cmd.MouseSet, true)]
    [InlineData(Cmd.MouseReset, true)]
    [InlineData(Cmd.LockSet, true)]
    [InlineData(Cmd.LockClear, true)]
    [InlineData(Cmd.MessageShow, true)]
    [InlineData(Cmd.MessageClear, true)]
    [InlineData(Cmd.WebsiteOpen, true)]
    [InlineData(Cmd.ProgramOpen, true)]
    [InlineData(Cmd.ProcWindows, true)]
    [InlineData(Cmd.ProcCloseApps, true)]
    [InlineData(Cmd.DesktopLayoutReset, true)]
    [InlineData(Cmd.FsDeliver, true)]
    [InlineData(Cmd.FsFind, true)]
    [InlineData(Cmd.FsDeliveredDelete, true)]
    [InlineData(Cmd.Ping, false)]
    [InlineData(Cmd.PowerAction, false)]
    [InlineData(Cmd.SelfUpdate, false)]
    [InlineData(Cmd.InstallRun, false)]
    [InlineData(Cmd.StagingList, false)]
    [InlineData(Cmd.StagingDelete, false)]
    public void IsUserOnly_matches_the_commands_that_require_a_signed_in_user(string cmd, bool expected)
    {
        Assert.Equal(expected, Cmd.IsUserOnly(cmd));
    }

    [Fact]
    public void Response_Success_is_Ok_with_no_error()
    {
        var response = Response.Success();
        Assert.True(response.Ok);
        Assert.Null(response.Error);
    }

    [Fact]
    public void Response_Fail_is_not_Ok_and_carries_the_message()
    {
        var response = Response.Fail("something went wrong");
        Assert.False(response.Ok);
        Assert.Equal("something went wrong", response.Error);
    }

    [Fact]
    public void Request_round_trips_through_json_with_camelCase_property_names()
    {
        var request = new Request { Cmd = Cmd.CmdRun, Level = Levels.User };
        var json = JsonSerializer.Serialize(request, Protocol.Json);

        Assert.Contains("\"cmd\":", json);
        Assert.Contains("\"level\":", json);

        var roundTripped = JsonSerializer.Deserialize<Request>(json, Protocol.Json);
        Assert.NotNull(roundTripped);
        Assert.Equal(Cmd.CmdRun, roundTripped!.Cmd);
        Assert.Equal(Levels.User, roundTripped.Level);
    }

    [Fact]
    public void DiscoveryReply_defaults_to_the_current_protocol_version_and_port()
    {
        var reply = new DiscoveryReply { ClientId = "installation-1", Mac = "001122AABBCC" };
        Assert.Equal(Protocol.Version, reply.Version);
        Assert.Equal(Protocol.DefaultTcpPort, reply.TcpPort);
        var json = JsonSerializer.Serialize(reply, Protocol.Json);
        Assert.DoesNotContain("userName", json, StringComparison.OrdinalIgnoreCase);
        var roundTripped = JsonSerializer.Deserialize<DiscoveryReply>(json, Protocol.Json);
        Assert.Equal("installation-1", roundTripped!.ClientId);
        Assert.Equal("001122AABBCC", roundTripped.Mac);
    }

    [Fact]
    public void Legacy_protocol_version_gates_commands_added_later()
    {
        Assert.False(Protocol.SupportsCommand(Cmd.WindowsPreferencesSet, "1.1", [], false));
        Assert.True(Protocol.SupportsCommand(Cmd.WallpaperReset, "1.1", [], false));
    }

    [Fact]
    public void Advertised_capabilities_take_precedence_over_version_fallback()
    {
        Assert.True(Protocol.SupportsCommand(Cmd.WindowsPreferencesSet, "1.2", [Cmd.WindowsPreferencesSet], true));
        Assert.False(Protocol.SupportsCommand(Cmd.LanguageInstall, "1.2", [Cmd.WindowsPreferencesSet], true));
        Assert.True(Protocol.SupportsCommand(Cmd.WallpaperReset, "1.2", [Cmd.WindowsPreferencesSet], true));
    }

    [Fact]
    public void Desktop_management_commands_require_protocol_1_3_and_explicit_capabilities()
    {
        Assert.Equal("1.3", Protocol.RequiredVersion(Cmd.WallpaperColor));
        Assert.False(Protocol.SupportsCommand(Cmd.WallpaperColor, "1.2", [], false));
        Assert.True(Protocol.SupportsCommand(Cmd.WallpaperColor, "1.3", [Cmd.WallpaperColor], true));
        Assert.False(Protocol.SupportsCommand(Cmd.WindowsThemeSet, "1.3", [Cmd.WindowsThemesList], true));
        Assert.Equal("1.4", Protocol.RequiredVersion(Cmd.WindowsUpdateScan));
        Assert.False(Protocol.SupportsCommand(Cmd.WindowsUpdateScan, "1.3", [], false));
    }

    [Fact]
    public void Exam_mode_is_a_separate_protocol_1_5_capability()
    {
        Assert.Equal("1.5", Protocol.RequiredVersion(Cmd.ExamModeSet));
        Assert.Equal("1.5", Protocol.RequiredVersion(Cmd.ExamModeClear));
        Assert.False(Protocol.SupportsCommand(Cmd.ExamModeSet, "1.4", [], false));
        Assert.True(Protocol.SupportsCommand(Cmd.ExamModeSet, "1.5", [Cmd.ExamModeSet], true));
        Assert.False(Protocol.SupportsCommand(Cmd.ExamModeClear, "1.5", [Cmd.ExamModeSet], true));
    }

    [Fact]
    public void Windows_update_pause_status_is_a_protocol_1_6_capability()
    {
        Assert.Equal("1.6", Protocol.RequiredVersion(Cmd.WindowsUpdateStatus));
        Assert.False(Protocol.SupportsCommand(Cmd.WindowsUpdateStatus, "1.5", [], false));
        Assert.True(Protocol.SupportsCommand(Cmd.WindowsUpdateStatus, "1.6", [Cmd.WindowsUpdateStatus], true));
    }

    [Fact]
    public void Effective_interface_language_is_a_protocol_1_6_user_capability()
    {
        Assert.Equal("1.6", Protocol.RequiredVersion(Cmd.WindowsPreferencesGet));
        Assert.True(Cmd.IsUserOnly(Cmd.WindowsPreferencesGet));
        Assert.False(Protocol.SupportsCommand(Cmd.WindowsPreferencesGet, "1.5", [], false));
        Assert.True(Protocol.SupportsCommand(Cmd.WindowsPreferencesGet, "1.6", [Cmd.WindowsPreferencesGet], true));
    }

    [Theory]
    [InlineData(Cmd.StagingList)]
    [InlineData(Cmd.StagingDelete)]
    public void Host_private_staging_management_is_protocol_1_7(string command)
    {
        Assert.Equal("1.7", Protocol.RequiredVersion(command));
        Assert.False(Protocol.SupportsCommand(command, "1.6", [], false));
        Assert.True(Protocol.SupportsCommand(command, "1.7", [command], true));
    }

    [Theory]
    [InlineData(Cmd.ScreenSaverGet)]
    [InlineData(Cmd.ScreenSaverSet)]
    [InlineData(Cmd.ScreenSaverReset)]
    public void Screen_saver_management_is_a_protocol_1_8_user_capability(string command)
    {
        Assert.Equal("1.8", Protocol.RequiredVersion(command));
        Assert.True(Cmd.IsUserOnly(command));
        Assert.Contains(command, Protocol.Capabilities);
        Assert.False(Protocol.SupportsCommand(command, "1.7", [], false));
        Assert.False(Protocol.SupportsCommand(command, "1.8", [], true));
        Assert.True(Protocol.SupportsCommand(command, "1.8", [command], true));
    }

    [Theory]
    [InlineData(Cmd.PowerDisplayGet)]
    [InlineData(Cmd.PowerDisplaySet)]
    public void Display_power_is_a_protocol_1_9_machine_wide_admin_capability(string command)
    {
        Assert.Equal("1.9", Protocol.RequiredVersion(command));
        // One power plan for the whole PC: it must reach the elevated service, not the user helper.
        Assert.False(Cmd.IsUserOnly(command));
        Assert.Contains(command, Protocol.Capabilities);
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(command));
        // A 1.8 client that does not advertise it must be told to update, not sent the command.
        Assert.False(Protocol.SupportsCommand(command, "1.8", [Cmd.ScreenSaverSet], true));
        Assert.True(Protocol.SupportsCommand(command, "1.9", [command], true));
    }

    [Fact]
    public void Waking_the_screen_is_a_protocol_1_10_classroom_action_of_the_signed_in_user()
    {
        Assert.Equal("1.10", Protocol.RequiredVersion(Cmd.DisplayWake));
        // The screen saver and the display belong to the user's session, so the helper handles it.
        Assert.True(Cmd.IsUserOnly(Cmd.DisplayWake));
        Assert.Contains(Cmd.DisplayWake, Protocol.Capabilities);
        Assert.Equal(OperatorRole.Teacher, CommandAccess.RequiredRole(Cmd.DisplayWake));
        // 1.10 is later than 1.9 although it sorts before it as text.
        Assert.False(Protocol.SupportsCommand(Cmd.DisplayWake, "1.9", [], false));
        Assert.True(Protocol.SupportsCommand(Cmd.DisplayWake, "1.10", [], false));
        Assert.False(Protocol.SupportsCommand(Cmd.DisplayWake, "1.9", [Cmd.PowerDisplaySet], true));
        Assert.True(Protocol.SupportsCommand(Cmd.DisplayWake, "1.10", [Cmd.DisplayWake], true));
    }

    [Theory]
    [InlineData(Cmd.RecycleBinEmpty)]
    [InlineData(Cmd.RecycleBinNameReset)]
    public void Recycle_Bin_actions_are_administrator_actions_in_the_signed_in_users_session(string command)
    {
        Assert.Equal("1.10", Protocol.RequiredVersion(command));
        // Every account has its own bin and its own name for it; the service's would be the wrong ones.
        Assert.True(Cmd.IsUserOnly(command));
        Assert.Contains(command, Protocol.Capabilities);
        // Neither can be undone, so a Teacher key is not enough.
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(command));
        Assert.False(Protocol.SupportsCommand(command, "1.9", [], false));
        Assert.True(Protocol.SupportsCommand(command, "1.10", [command], true));
    }

    [Fact]
    public void A_reset_name_is_reported_with_the_name_it_replaced()
    {
        var renamed = new System.Text.Json.Nodes.JsonObject { [RecycleBinRules.CustomName] = "Trash" };

        Assert.Equal("name reset to the Windows default (was \"Trash\")", RecycleBinRules.DescribeName(renamed));
        Assert.Equal("the Recycle Bin already had its Windows name", RecycleBinRules.DescribeName(new System.Text.Json.Nodes.JsonObject()));
    }

    [Theory]
    [InlineData(0, 0, "the Recycle Bin was already empty")]
    [InlineData(3, 512, "3 item(s) removed, 512 bytes freed")]
    [InlineData(12, 5 * 1024 * 1024, "12 item(s) removed, 5 MB freed")]
    [InlineData(1, 1610612736, "1 item(s) removed, 1.5 GB freed")]
    public void An_emptied_Recycle_Bin_is_reported_by_what_it_held(long items, long bytes, string expected)
    {
        var data = new System.Text.Json.Nodes.JsonObject { [RecycleBinRules.Items] = items, [RecycleBinRules.Bytes] = bytes };

        Assert.Equal(expected, RecycleBinRules.Describe(data));
    }

    [Theory]
    [InlineData(false, false, "display woken, no screen saver was running")]
    [InlineData(true, false, "screen saver stopped")]
    public void A_wake_answer_says_what_happened_on_the_computer(bool wasRunning, bool signInScreen, string expected)
    {
        var data = new System.Text.Json.Nodes.JsonObject
        {
            [DisplayWakeRules.ScreenSaverWasRunning] = wasRunning,
            [DisplayWakeRules.SignInScreen] = signInScreen,
        };

        Assert.Equal(expected, DisplayWakeRules.Describe(data));
        // Nothing can sign the student in from here, so the answer has to say when that is still needed.
        Assert.Contains("someone has to sign in", DisplayWakeRules.Describe(wasRunning, signInScreen: true));
        Assert.Equal("display woken, no screen saver was running", DisplayWakeRules.Describe(new System.Text.Json.Nodes.JsonObject()));
    }

    [Theory]
    [InlineData(Cmd.SignInImageSet)]
    [InlineData(Cmd.SignInImageDefault)]
    public void Sign_in_screen_image_is_a_protocol_1_8_machine_wide_capability(string command)
    {
        Assert.Equal("1.8", Protocol.RequiredVersion(command));
        // One setting for every user of the PC: it must reach the elevated service, not the user helper.
        Assert.False(Cmd.IsUserOnly(command));
        Assert.Contains(command, Protocol.Capabilities);
        Assert.False(Protocol.SupportsCommand(command, "1.7", [], false));
        Assert.True(Protocol.SupportsCommand(command, "1.8", [command], true));
    }

    [Fact]
    public void Delivered_file_undo_is_a_protocol_1_7_user_capability()
    {
        Assert.Equal("1.7", Protocol.RequiredVersion(Cmd.FsDeliveredDelete));
        Assert.True(Cmd.IsUserOnly(Cmd.FsDeliveredDelete));
        Assert.True(Protocol.SupportsCommand(Cmd.FsDeliveredDelete, "1.7", [Cmd.FsDeliveredDelete], true));
    }

    [Fact]
    public void Website_block_reset_is_an_explicit_protocol_1_5_capability()
    {
        Assert.Equal("1.5", Protocol.RequiredVersion(Cmd.BlockHostsReset));
        Assert.True(Protocol.SupportsCommand(Cmd.BlockHostsReset, "1.5", [Cmd.BlockHostsReset], true));
        Assert.False(Protocol.SupportsCommand(Cmd.BlockHostsReset, "1.5", [Cmd.BlockHostsSet], true));
    }

    [Fact]
    public void Remote_managed_wallpaper_selection_is_a_protocol_1_5_capability()
    {
        Assert.Equal("1.5", Protocol.RequiredVersion(Cmd.WallpaperManagedSet));
        Assert.False(Protocol.SupportsCommand(Cmd.WallpaperManagedSet, "1.4", [], false));
        Assert.True(Protocol.SupportsCommand(Cmd.WallpaperManagedSet, "1.5", [Cmd.WallpaperManagedSet], true));
        Assert.False(Protocol.SupportsCommand(Cmd.WallpaperManagedSet, "1.5", [Cmd.WallpaperManagedList], true));
    }

    [Fact]
    public void Remote_managed_wallpaper_preview_is_a_protocol_1_5_capability()
    {
        Assert.Equal("1.5", Protocol.RequiredVersion(Cmd.WallpaperManagedPreview));
        Assert.False(Protocol.SupportsCommand(Cmd.WallpaperManagedPreview, "1.4", [], false));
        Assert.True(Protocol.SupportsCommand(Cmd.WallpaperManagedPreview, "1.5", [Cmd.WallpaperManagedPreview], true));
    }

    [Fact]
    public void Clearing_host_messages_is_a_protocol_1_5_capability()
    {
        Assert.Equal("1.5", Protocol.RequiredVersion(Cmd.MessageClear));
        Assert.True(Protocol.SupportsCommand(Cmd.MessageClear, "1.5", [Cmd.MessageClear], true));
        Assert.False(Protocol.SupportsCommand(Cmd.MessageClear, "1.5", [Cmd.MessageShow], true));
    }

    [Theory]
    [InlineData(Cmd.WebsiteOpen)]
    [InlineData(Cmd.ProgramOpen)]
    public void Remote_launch_commands_are_protocol_1_5_capabilities(string command)
    {
        Assert.Equal("1.5", Protocol.RequiredVersion(command));
        Assert.True(Protocol.SupportsCommand(command, "1.5", [command], true));
        Assert.False(Protocol.SupportsCommand(command, "1.4", [], false));
    }

    [Fact]
    public void Open_window_inventory_is_a_protocol_1_5_user_capability()
    {
        Assert.Equal("1.5", Protocol.RequiredVersion(Cmd.ProcWindows));
        Assert.True(Cmd.IsUserOnly(Cmd.ProcWindows));
        Assert.True(Protocol.SupportsCommand(Cmd.ProcWindows, "1.5", [Cmd.ProcWindows], true));
    }

    [Fact]
    public void Closing_visible_apps_is_a_protocol_1_5_user_capability()
    {
        Assert.Equal("1.5", Protocol.RequiredVersion(Cmd.ProcCloseApps));
        Assert.True(Cmd.IsUserOnly(Cmd.ProcCloseApps));
        Assert.True(Protocol.SupportsCommand(Cmd.ProcCloseApps, "1.5", [Cmd.ProcCloseApps], true));
        Assert.False(Protocol.SupportsCommand(Cmd.ProcCloseApps, "1.5", [Cmd.ProcWindows], true));
    }

    [Fact]
    public void Desktop_layout_reset_is_a_protocol_1_5_capability()
    {
        Assert.Equal("1.5", Protocol.RequiredVersion(Cmd.DesktopLayoutReset));
        Assert.True(Protocol.SupportsCommand(Cmd.DesktopLayoutReset, "1.5", [Cmd.DesktopLayoutReset], true));
        Assert.False(Protocol.SupportsCommand(Cmd.DesktopLayoutReset, "1.4", [], false));
    }

    [Fact]
    public void Unsupported_response_is_structured_for_host_guidance()
    {
        var response = Response.Unsupported(Cmd.WindowsPreferencesSet);
        Assert.False(response.Ok);
        Assert.Equal("unsupported_command", response.Data["code"]!.GetValue<string>());
        Assert.Equal("1.2", response.Data["requiredProtocol"]!.GetValue<string>());
    }
}
