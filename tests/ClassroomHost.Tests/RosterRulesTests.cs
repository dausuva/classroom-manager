// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>The decisions of the computer list and of a status check (T-253 phase 2), without a window: the network and room filters, the rooms, moving computers into a room, the summary line, and what each failure says.</summary>
public sealed class RosterRulesTests
{
    static Discovery.NetworkSubnet Subnet() => new("10.42.4.0/24", "Ethernet", "Intel NIC", NetworkInterfaceType.Ethernet, IPAddress.Parse("10.42.4.9"),
        IPAddress.Parse("10.42.4.0"), 24, IPAddress.Parse("10.42.4.255"));

    // ---- network and rooms ------------------------------------------------------------------

    [Fact]
    public void A_computer_belongs_to_the_selected_network_by_its_address_and_a_host_name_always_stays_visible()
    {
        var subnet = Subnet();
        Assert.True(RosterRules.InNetwork("10.42.4.77", subnet));
        Assert.False(RosterRules.InNetwork("10.42.5.77", subnet));
        Assert.False(RosterRules.InNetwork("192.168.1.5", subnet));
        Assert.True(RosterRules.InNetwork("pc-04.school.example", subnet));
        Assert.True(RosterRules.InNetwork("PC-04", subnet));
        Assert.True(RosterRules.InNetwork("192.168.1.5", null));
        Assert.True(RosterRules.InNetwork("anything", null));
    }

    [Fact]
    public void A_room_filter_matches_in_any_case_and_an_empty_one_matches_every_room()
    {
        Assert.True(RosterRules.InRoom("Lab 1", "lab 1"));
        Assert.True(RosterRules.InRoom("", ""));
        Assert.True(RosterRules.InRoom("Lab 2", ""));
        Assert.False(RosterRules.InRoom("Lab 2", "Lab 1"));
        Assert.False(RosterRules.InRoom("", "Lab 1"));
    }

    [Fact]
    public void The_rooms_are_listed_once_each_in_alphabetical_order_without_the_empty_one()
    {
        Assert.Equal(["Lab 1", "lab 3", "Library"], RosterRules.Rooms(["Library", "Lab 1", "", "LAB 1", "lab 3", ""]));
        Assert.Empty(RosterRules.Rooms(["", ""]));
    }

    [Fact]
    public void The_room_box_lists_all_rooms_first_keeps_a_known_room_selected_and_falls_back_when_the_room_is_gone()
    {
        var (items, index, room) = RosterRules.RoomChoices(["Lab 1", "Lab 2"], "Lab 2");
        Assert.Equal(["(all rooms)", "Lab 1", "Lab 2"], items);
        Assert.Equal((2, "Lab 2"), (index, room));
        Assert.Equal((0, ""), (RosterRules.RoomChoices(["Lab 1"], "").SelectedIndex, RosterRules.RoomChoices(["Lab 1"], "").Room));
        var gone = RosterRules.RoomChoices(["Lab 1"], "Old room");
        Assert.Equal((0, ""), (gone.SelectedIndex, gone.Room));
        var otherCase = RosterRules.RoomChoices(["Lab 1"], "LAB 1");
        Assert.Equal("LAB 1", otherCase.Room);   // kept as typed; the case-sensitive lookup then shows all rooms, as before
        Assert.Equal(0, otherCase.SelectedIndex);
    }

    [Fact]
    public void Putting_computers_in_a_room_asks_for_a_new_check_and_taking_them_out_clears_what_they_last_said()
    {
        var a = new ClientInfo { Name = "A", Room = "", Level = "ok", Status = "Online", Ping = "3 ms", Clock = "10:00", Uptime = "2 h", LastSeen = "now", ClockOffsetSec = 7.5 };
        var b = new ClientInfo { Name = "B", Room = "Lab 1", Level = "ok", Status = "Online", Ping = "1 ms", Clock = "10:00", Uptime = "1 h", LastSeen = "now", ClockOffsetSec = 1 };

        RosterRules.AssignRoom([a], "Lab 2");
        Assert.Equal(("Lab 2", "Checking...", "ok"), (a.Room, a.Status, a.Level));   // its last answers stay until the new check replaces them

        RosterRules.AssignRoom([b], "");
        Assert.Equal(("", "", "Discovered - assign to a room to monitor"), (b.Room, b.Level, b.Status));
        Assert.Equal(("", "", "", ""), (b.Ping, b.Clock, b.Uptime, b.LastSeen));
        Assert.Null(b.ClockOffsetSec);
    }

    // ---- the summary line -------------------------------------------------------------------

    static ClientInfo Computer(string level, string room = "Lab 1", double? offset = null) => new() { Room = room, Level = level, ClockOffsetSec = offset };

    [Fact]
    public void The_summary_counts_the_computers_of_each_kind_and_says_what_is_not_watched()
    {
        var now = new DateTime(2026, 11, 3, 9, 5, 7);
        ClientInfo[] list = [Computer("ok"), Computer("ok", offset: 6), Computer("idle"), Computer("warn"), Computer("bad"), Computer(""), Computer("", room: "")];
        Assert.Equal("3 online  |  1 with nobody signed in  |  1 need attention  |  1 wrong key  |  1 offline  |  1 discovered, not monitored (no room)  |  1 with clock off by more than 5 s  (of 7 on selected network, updated 09:05:07)",
            RosterRules.Summary(list, false, now));
    }

    [Fact]
    public void A_quiet_room_has_only_the_online_and_offline_counts_and_a_teacher_only_key_is_pointed_out()
    {
        var now = new DateTime(2026, 11, 3, 14, 0, 0);
        Assert.Equal("2 online  |  0 offline  (of 2 on selected network, updated 14:00:00)", RosterRules.Summary([Computer("ok"), Computer("ok", offset: -5)], false, now));
        Assert.EndsWith("  |  Teacher key only: no Admin key is set, the computers refuse administrator actions  (of 1 on selected network, updated 14:00:00)",
            RosterRules.Summary([Computer("ok")], true, now));
        Assert.Equal("0 online  |  0 offline  (of 0 on selected network, updated 14:00:00)", RosterRules.Summary([], false, now));
        Assert.Contains("1 with clock off by more than 5 s", RosterRules.Summary([Computer("ok", offset: -5.1)], false, now));
    }

    [Fact]
    public void The_network_line_names_the_subnet_and_why_when_it_is_not_the_one_chosen()
    {
        var subnet = Subnet();
        var automatic = RosterRules.NetworkScope(new Discovery.SubnetSelection(subnet, false), "");
        Assert.Equal("Network: 10.42.4.0/24 (automatic LAN)", automatic.Text);
        Assert.Contains("limited to 10.42.4.0/24 - Ethernet (10.42.4.9)", automatic.ToolTip);
        Assert.Equal("Network: 10.42.4.0/24 (Ethernet)", RosterRules.NetworkScope(new Discovery.SubnetSelection(subnet, false), "10.42.4.0/24").Text);
        var fallback = RosterRules.NetworkScope(new Discovery.SubnetSelection(subnet, true), "172.20.0.0/16");
        Assert.Equal("Network: 10.42.4.0/24 (fallback)", fallback.Text);
        Assert.Contains("Saved subnet 172.20.0.0/16 is unavailable", fallback.ToolTip);
        var none = RosterRules.NetworkScope(new Discovery.SubnetSelection(null, false), "");
        Assert.Equal("Network: no connected IPv4 subnet; showing all saved computers", none.Text);
        Assert.Contains("Connect an IPv4 network", none.ToolTip);
    }

    [Fact]
    public void A_new_computer_name_is_refused_for_the_rules_for_being_the_same_and_for_being_taken()
    {
        Assert.Null(RosterRules.RenameProblem("LAB1-PC05", "LAB1-PC04", ["LAB1-PC01"]));
        Assert.Equal("The computer already has this name.", RosterRules.RenameProblem("lab1-pc04", "LAB1-PC04", []));
        Assert.Equal("Another computer in this list already has this name.", RosterRules.RenameProblem("lab1-pc01", "LAB1-PC04", ["LAB1-PC01"]));
        Assert.NotNull(RosterRules.RenameProblem("not a valid name!", "LAB1-PC04", []));
        Assert.NotNull(RosterRules.RenameProblem("", "LAB1-PC04", []));
    }

    // ---- what a status check says -----------------------------------------------------------

    [Fact]
    public void A_wrong_key_and_a_Host_with_no_key_need_no_ping_and_anything_else_does()
    {
        Assert.Equal(("bad", "Wrong shared key"), StatusOutcomes.Immediate(new AuthException("no"), hostHasKeys: true));
        Assert.Equal(("bad", "No key is set"), StatusOutcomes.Immediate(new InvalidOperationException("No key is set"), hostHasKeys: false));
        Assert.Null(StatusOutcomes.Immediate(new InvalidOperationException("something else"), hostHasKeys: true));
        Assert.Null(StatusOutcomes.Immediate(new TimeoutException(), hostHasKeys: true));
        Assert.Null(StatusOutcomes.Immediate(new IOException("reset"), hostHasKeys: false));
    }

    [Fact]
    public void A_computer_that_answers_a_ping_is_on_and_has_a_connection_or_client_problem_and_one_that_does_not_is_offline()
    {
        var tls = StatusOutcomes.AfterPing(new AuthenticationException("the certificate is not trusted"), true);
        Assert.Equal("warn", tls.Level);
        Assert.StartsWith("PC is on and the client answered, but the connection failed", tls.Status);
        Assert.EndsWith("(the certificate is not trusted)", tls.Status);
        Assert.StartsWith("PC is on and the client answered", StatusOutcomes.AfterPing(new IOException("reset"), true).Status);
        Assert.Equal(("warn", "PC is on, but the client is not reachable (service stopped, not installed, or blocked by firewall)"), StatusOutcomes.AfterPing(new SocketException(10061), true));
        Assert.Equal(("", "Offline (no reply)"), StatusOutcomes.AfterPing(new TimeoutException(), false));
        Assert.Equal(("", "Offline (no reply)"), StatusOutcomes.AfterPing(new SocketException(10060), false));
        Assert.Equal(("", "Offline: boom"), StatusOutcomes.AfterPing(new InvalidDataException("boom"), false));
        Assert.Equal(("", "Offline: x"), StatusOutcomes.AfterPing(new AuthenticationException("x"), false));
    }

    [Fact]
    public void The_key_beside_a_computer_is_marked_when_it_is_the_old_one_or_a_Teacher_key()
    {
        Assert.Equal("Lab key", StatusOutcomes.KeyLabel("Lab key", false, "Admin"));
        Assert.Equal("Lab key (old key)", StatusOutcomes.KeyLabel("Lab key", true, ""));
        Assert.Equal("Room 12 (Teacher)", StatusOutcomes.KeyLabel("Room 12", false, "Teacher"));
        Assert.Equal("Room 12 (old key) (Teacher)", StatusOutcomes.KeyLabel("Room 12", true, "Teacher"));
    }

    [Fact]
    public void A_computer_still_on_the_old_key_is_told_how_to_finish_the_change_or_why_it_could_not_be()
    {
        Assert.Equal(("warn", "Online - still on the old shared key (use Rotate key)"), StatusOutcomes.OldKey(autoFinish: false, null));
        Assert.Equal(("warn", "Online - still on the old shared key: the computer refused"), StatusOutcomes.OldKey(autoFinish: true, "the computer refused"));
        Assert.Equal("Address 10.42.4.9 now belongs to another computer (card AA:BB:CC:DD:EE:FF); looking for this one", StatusOutcomes.AddressTaken("10.42.4.9", "AA:BB:CC:DD:EE:FF"));
    }
}
