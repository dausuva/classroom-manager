// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public class WindowsReleaseTests
{
    [Theory]
    [InlineData("25H2", "25H2")]
    [InlineData(" 24h2 ", "24H2")]
    [InlineData("22H1", "22H1")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("25H3", "")]
    [InlineData("2025", "")]
    [InlineData("Windows 11", "")]
    [InlineData("25H2 ; calc", "")]
    public void Only_a_version_name_is_accepted_as_a_target(string? text, string expected)
    {
        Assert.Equal(expected, WindowsRelease.Normalize(text));
    }

    [Fact]
    public void Versions_compare_by_year_and_half_and_unknown_text_decides_nothing()
    {
        Assert.True(WindowsRelease.Compare("24H2", "25H2") < 0);
        Assert.True(WindowsRelease.Compare("22H2", "22H1") > 0);
        Assert.Equal(0, WindowsRelease.Compare("25h2", "25H2"));
        Assert.Equal(0, WindowsRelease.Compare("", "25H2"));
        Assert.Equal(0, WindowsRelease.Compare("1809", "25H2"));
    }

    [Theory]
    [InlineData(19045, "Windows 10")]
    [InlineData(22000, "Windows 11")]
    [InlineData(26100, "Windows 11")]
    public void The_build_number_tells_Windows_10_from_11(int build, string product)
    {
        Assert.Equal(product, WindowsRelease.ProductOfBuild(build));
    }

    [Theory]
    [InlineData("Professional", true)]
    [InlineData("Education", true)]
    [InlineData("Enterprise", true)]
    [InlineData("Core", false)]                   // Home
    [InlineData("CoreSingleLanguage", false)]
    public void Home_editions_do_not_take_a_target_version(string edition, bool takes)
    {
        Assert.Equal(takes, WindowsRelease.EditionTakesTarget(edition));
    }

    [Theory]
    [InlineData("Windows 11, version 25H2", "", true)]
    [InlineData("Feature update to Windows 10, version 22H2", "", true)]
    [InlineData("Something Microsoft renamed", WindowsRelease.UpgradesCategory, true)]
    [InlineData("2026-10 Cumulative Update for Windows 11, version 25H2 for x64-based Systems (KB5127648)", "", false)]
    [InlineData("Windows 11, version 25H2 preview (KB5127000)", "", false)]
    [InlineData("Realtek SoftwareComponent Driver Update (1.0.1011.0)", "ebfc1fc5-71a4-4f7b-9aca-3b9a503104a0", false)]
    public void A_new_Windows_version_is_told_apart_from_a_monthly_update(string title, string category, bool feature)
    {
        Assert.Equal(feature, WindowsRelease.IsFeatureUpdate(title, category.Length > 0 ? [category] : []));
    }

    [Fact]
    public void A_status_reads_as_one_line_and_an_older_client_reports_nothing()
    {
        var status = new JsonObject
        {
            [WindowsRelease.Product] = "Windows 11", [WindowsRelease.Version] = "24H2", [WindowsRelease.Build] = "26100.2033",
            [WindowsRelease.TargetVersion] = "25H2", [WindowsRelease.RestartPending] = true,
        };
        Assert.Equal("Windows 11 24H2 (build 26100.2033)", WindowsRelease.Describe(status));
        Assert.Equal("moving to 25H2 · restart pending", WindowsRelease.DescribeTarget(status));

        status[WindowsRelease.Version] = "25H2";
        status[WindowsRelease.RestartPending] = false;
        Assert.Equal("kept on 25H2", WindowsRelease.DescribeTarget(status));

        var older = new JsonObject { ["paused"] = false, ["untilUtc"] = "" };
        Assert.Equal("", WindowsRelease.Describe(older));
        Assert.Equal("", WindowsRelease.DescribeTarget(older));
    }

    [Fact]
    public void Setting_the_target_is_an_administrator_request_of_the_service()
    {
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(Cmd.WindowsUpdateTargetSet));
        Assert.Equal("1.10", Protocol.RequiredVersion(Cmd.WindowsUpdateTargetSet));
        Assert.False(Cmd.IsUserOnly(Cmd.WindowsUpdateTargetSet));
        Assert.Contains(Cmd.WindowsUpdateTargetSet, Protocol.Capabilities);
    }
}
