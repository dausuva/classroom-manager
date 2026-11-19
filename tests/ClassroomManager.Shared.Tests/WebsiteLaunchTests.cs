// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>T-178: opening a website maximized and in a chosen browser (protocol 1.15).</summary>
public sealed class WebsiteLaunchTests
{
    const string Url = "https://example.com/lesson?a=1&b=2";

    static BrowserCatalog.Installed Browser(string id, string path) => new(id, id, path, "1.0", "", "", true);

    static readonly BrowserCatalog.Installed[] Installed =
    [
        Browser("edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
        Browser("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
        Browser("firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe"),
    ];

    [Fact]
    public void The_protocol_version_moved_with_the_feature_and_the_two_numbers_agree()
    {
        Assert.Equal(Protocol.Version, $"{Protocol.Major}.{Protocol.Minor}");
        Assert.True(Protocol.IsAtLeast(Protocol.Version, WebsiteLaunch.RequiredVersion));
    }

    [Theory]
    [InlineData("1.15", true)]
    [InlineData("1.16", true)]
    [InlineData("1.14", false)]
    [InlineData("1.0", false)]
    [InlineData("2.15", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a version", false)]
    public void Only_a_client_with_protocol_1_15_or_later_understands_the_options(string? version, bool expected) =>
        Assert.Equal(expected, WebsiteLaunch.Supported(version));

    [Fact]
    public void No_browser_chosen_means_the_default_browser_through_windows()
    {
        var plan = WebsiteLaunch.Choose(Url, "", maximized: true, Installed);

        Assert.True(plan.UsesDefaultBrowser);
        Assert.False(plan.FellBack);
        Assert.Equal(Url, plan.Program);
        Assert.Equal("", plan.Arguments);
    }

    [Theory]
    [InlineData("edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    public void A_chromium_browser_gets_a_new_window_and_the_switch_for_maximized(string id, string program)
    {
        var plan = WebsiteLaunch.Choose(Url, id, maximized: true, Installed);

        Assert.False(plan.UsesDefaultBrowser);
        Assert.Equal(program, plan.Program);
        Assert.Equal($"--new-window --start-maximized \"{Url}\"", plan.Arguments);
        Assert.Equal(id, plan.BrowserId);
    }

    [Fact]
    public void Without_the_maximized_option_the_switch_is_left_out()
    {
        var plan = WebsiteLaunch.Choose(Url, "chrome", maximized: false, Installed);

        Assert.Equal($"--new-window \"{Url}\"", plan.Arguments);
    }

    [Fact]
    public void Firefox_has_no_maximize_switch_so_it_only_gets_a_new_window()
    {
        var plan = WebsiteLaunch.Choose(Url, "firefox", maximized: true, Installed);

        Assert.Equal($"-new-window \"{Url}\"", plan.Arguments);
    }

    [Theory]
    [InlineData("brave")]      // known, but not installed on this computer
    [InlineData("netscape")]   // not known at all
    [InlineData("  ")]
    public void A_browser_that_is_not_there_falls_back_to_the_default_one_without_an_error(string id)
    {
        var plan = WebsiteLaunch.Choose(Url, id, maximized: true, Installed);

        Assert.True(plan.UsesDefaultBrowser);
        Assert.Equal(id.Trim().Length > 0, plan.FellBack);
        Assert.Equal(Url, plan.Program);
    }

    [Fact]
    public void The_browser_is_found_whatever_the_case_of_its_id()
    {
        Assert.Equal("edge", WebsiteLaunch.Choose(Url, "EDGE", true, Installed).BrowserId);
    }

    [Fact]
    public void A_quote_in_an_address_cannot_end_the_quoted_argument()
    {
        var plan = WebsiteLaunch.Choose("https://example.com/a\"--user-data-dir=x", "chrome", true, Installed);

        Assert.DoesNotContain("a\"--", plan.Arguments);
        Assert.EndsWith("%22--user-data-dir=x\"", plan.Arguments);
    }

    [Fact]
    public void A_client_with_options_gets_them_and_an_older_one_gets_only_the_address()
    {
        var modern = WebsiteLaunch.Args(Url, "edge", true, clientSupportsOptions: true);
        Assert.Equal("edge", modern[WebsiteLaunch.Browser]!.GetValue<string>());
        Assert.True(modern[WebsiteLaunch.Maximized]!.GetValue<bool>());

        var legacy = WebsiteLaunch.Args(Url, "edge", true, clientSupportsOptions: false);
        Assert.Equal([WebsiteLaunch.Url], legacy.Select(pair => pair.Key));
    }

    [Fact]
    public void The_default_browser_is_not_named_in_the_request()
    {
        var args = WebsiteLaunch.Args(Url, "", false, clientSupportsOptions: true);

        Assert.False(args.ContainsKey(WebsiteLaunch.Browser));
        Assert.False(args[WebsiteLaunch.Maximized]!.GetValue<bool>());
    }
}
