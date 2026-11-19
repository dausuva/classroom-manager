// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Client;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-164: the name a person knows a program by, next to its process name.</summary>
public sealed class ProcessDescriptionTests
{
    const string Edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

    [Fact]
    public void The_file_description_is_used_when_it_says_more_than_the_file_name()
    {
        Assert.Equal("Microsoft Edge", ProcessHandler.DescriptionOf("Microsoft Edge", "Microsoft Edge WebView", Edge));
    }

    [Theory]
    [InlineData("msedge")]
    [InlineData("MSEDGE")]
    [InlineData("msedge.exe")]
    [InlineData("  ")]
    [InlineData(null)]
    public void A_description_that_only_repeats_the_file_name_gives_way_to_the_product_name(string? description)
    {
        Assert.Equal("Microsoft Edge", ProcessHandler.DescriptionOf(description, "Microsoft Edge", Edge));
    }

    [Fact]
    public void Nothing_useful_gives_an_empty_name_not_a_repeat_of_the_process()
    {
        Assert.Equal("", ProcessHandler.DescriptionOf("svchost.exe", "svchost", @"C:\Windows\System32\svchost.exe"));
        Assert.Equal("", ProcessHandler.DescriptionOf(null, null, Edge));
        Assert.Equal("", ProcessHandler.DescriptionOf("", "", ""));
    }

    [Fact]
    public void Spaces_and_stray_line_breaks_are_dropped()
    {
        Assert.Equal("Host Process for Windows Services", ProcessHandler.DescriptionOf("  Host Process\r\nfor Windows Services \r\n", null, @"C:\x\svchost.exe"));
    }

    [Fact]
    public void Every_process_in_the_list_carries_the_field_and_real_programs_have_a_name()
    {
        var processes = new ProcessHandler().List().Data["processes"]!.AsArray();

        Assert.All(processes, process => Assert.NotNull(process!["description"]));
        Assert.Contains(processes, process => process!["description"]!.GetValue<string>().Length > 0);
    }
}
