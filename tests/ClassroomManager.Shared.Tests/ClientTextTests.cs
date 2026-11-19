// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using Xunit;

namespace ClassroomManager.Shared.Tests;

public class ClientTextTests
{
    [Theory]
    [InlineData("lt-LT")]
    [InlineData("lt")]
    [InlineData("LT_lt")]
    public void A_Lithuanian_interface_gets_the_tray_notice_in_Lithuanian(string culture)
    {
        Assert.Equal("Classroom Manager – šį kompiuterį gali valdyti mokytojas", ClientText.TrayNotice(culture));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("lv-LV")]      // Latvian is not Lithuanian
    [InlineData("")]
    [InlineData(null)]
    public void Every_other_interface_gets_English(string? culture)
    {
        Assert.Equal("Classroom Manager - this computer can be managed by the teacher", ClientText.TrayNotice(culture));
    }

    [Fact]
    public void The_notice_fits_a_notification_area_tooltip()
    {
        Assert.All(new[] { "lt-LT", "en-US" }, culture => Assert.InRange(ClientText.TrayNotice(culture).Length, 1, 127));
    }
}
