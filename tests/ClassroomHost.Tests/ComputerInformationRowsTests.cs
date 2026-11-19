// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-172: the lines of Computer information, and the MAC address as Windows prints it.</summary>
public sealed class ComputerInformationRowsTests
{
    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff", "AA-BB-CC-DD-EE-FF")]
    [InlineData("AABBCCDDEEFF", "AA-BB-CC-DD-EE-FF")]
    [InlineData("aa-bb-cc-dd-ee-ff", "AA-BB-CC-DD-EE-FF")]
    [InlineData("aabb.ccdd.eeff", "AA-BB-CC-DD-EE-FF")]
    [InlineData("  0a:1b:2c:3d:4e:5f  ", "0A-1B-2C-3D-4E-5F")]
    public void A_mac_address_is_shown_in_capitals_with_hyphens(string value, string expected) =>
        Assert.Equal(expected, ComputerIdentity.FormatMac(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AA-BB-CC")]
    [InlineData("AA-BB-CC-DD-EE-FF-00")]
    [InlineData("GG-HH-II-JJ-KK-LL")]
    public void Something_that_is_not_a_mac_address_shows_nothing(string? value) =>
        Assert.Equal("", ComputerIdentity.FormatMac(value));

    [Fact]
    public void Showing_a_mac_address_does_not_change_the_form_that_is_stored_and_compared()
    {
        Assert.Equal("AABBCCDDEEFF", ComputerIdentity.NormalizeMac(ComputerIdentity.FormatMac("aa:bb:cc:dd:ee:ff")));
        Assert.True(ComputerIdentity.StableMatch("", ComputerIdentity.FormatMac("aa:bb:cc:dd:ee:ff"), "", "AABBCCDDEEFF"));
    }

    static ClientInfo Known() => new()
    {
        Name = "PC-07", Address = "192.0.2.7", Status = "Online", User = "student7", Mac = "AABBCCDDEEFF",
        WindowsVersion = "Windows 11 24H2", AppVersion = "1.2.0.408", Version = "1.14", Uptime = "1h 12m", LastSeen = "10:41:02",
    };

    [Fact]
    public void The_facts_a_teacher_looks_for_come_first_and_the_software_after_them()
    {
        var rows = ClientInfoWindow.BaseRows(new JsonObject(), Known());

        Assert.Equal(["Computer", "Status", "Signed-in user", "Address", "MAC address", "Uptime", "Windows", "Classroom Client", "Protocol", "Last seen"],
            rows.Select(row => row.Label));
        Assert.Equal("AA-BB-CC-DD-EE-FF", rows.Single(row => row.Label == "MAC address").Value);
    }

    [Fact]
    public void What_the_computer_says_now_wins_over_what_the_host_remembers()
    {
        var info = new JsonObject
        {
            ["machineName"] = "PC-07-NEW", ["userName"] = "anna", ["mac"] = "11:22:33:44:55:66", ["uptimeSec"] = 3 * 3600L + 5 * 60,
        };

        var rows = ClientInfoWindow.BaseRows(info, Known()).ToDictionary(row => row.Label, row => row.Value);

        Assert.Equal("PC-07-NEW", rows["Computer"]);
        Assert.Equal("anna", rows["Signed-in user"]);
        Assert.Equal("11-22-33-44-55-66", rows["MAC address"]);
        Assert.NotEqual("1h 12m", rows["Uptime"]);
        Assert.Equal("Online", rows["Status"]);
    }

    [Fact]
    public void A_fact_nobody_knows_yet_is_left_out_instead_of_leaving_an_empty_line()
    {
        var unseen = new ClientInfo { Name = "PC-09", Address = "192.0.2.9", Status = "Offline (no reply)" };

        var labels = ClientInfoWindow.BaseRows(new JsonObject(), unseen).Select(row => row.Label).ToList();

        Assert.Equal(["Computer", "Status", "Address"], labels);
    }
}
