// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Client;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-178: what the Client refuses before it starts any browser. Starting one is checked on a real PC.</summary>
public sealed class LaunchHandlerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    public async Task Only_a_complete_http_or_https_address_is_opened_whatever_the_options(string url)
    {
        var answer = await new LaunchHandler().OpenWebsiteAsync(new JsonObject
        {
            [WebsiteLaunch.Url] = url, [WebsiteLaunch.Browser] = "edge", [WebsiteLaunch.Maximized] = true,
        }, CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Contains("http", answer.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_request_without_a_browser_means_the_default_browser_as_before_protocol_1_15()
    {
        Assert.Equal("", LaunchHandler.BrowserId(new JsonObject { [WebsiteLaunch.Url] = "https://example.com/" }));
        Assert.Equal("", LaunchHandler.BrowserId(new JsonObject { [WebsiteLaunch.Browser] = " " }));
        Assert.Equal("edge", LaunchHandler.BrowserId(new JsonObject { [WebsiteLaunch.Browser] = "edge " }));
    }

    [Fact]
    public void The_window_helper_sees_the_windows_of_a_running_program_and_none_for_one_that_is_not_running()
    {
        Assert.Empty(WindowMaximizer.Windows("no-such-program-" + Guid.NewGuid().ToString("N")));
    }
}
