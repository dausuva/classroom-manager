// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public class BrowserSitesTests
{
    [Theory]
    [InlineData("https://www.example.com/watch?v=1", "example.com")]
    [InlineData("example.com/path", "example.com")]                 // browsers hide https:// and www.
    [InlineData("games.example.net", "games.example.net")]
    [InlineData("HTTP://Example.COM:8080/x", "example.com")]
    [InlineData("example.com:8080", "example.com")]
    [InlineData("https://user@mail.example.org/inbox#top", "mail.example.org")]
    [InlineData("https://xn--bcher-kva.example/", "xn--bcher-kva.example")]
    [InlineData("žaidimai.lt/naujienos", "žaidimai.lt")]
    public void The_domain_is_read_from_what_an_address_bar_shows(string shown, string expected)
    {
        Assert.Equal(expected, BrowserSites.DomainOf(shown));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("how to pass the exam")]          // a search being typed
    [InlineData("about:blank")]
    [InlineData("edge://settings/profiles")]
    [InlineData("chrome://newtab")]
    [InlineData("file:///C:/Users/student/page.html")]
    [InlineData("view-source:https://example.com")]
    [InlineData("localhost")]
    [InlineData("localhost:5000/admin")]
    [InlineData("192.168.1.10/login")]            // a block list of names cannot stop an address
    [InlineData("https://10.0.0.1:8443/")]
    [InlineData("-bad-.example.com")]
    [InlineData("exam_ple.com")]
    public void What_is_not_a_website_gives_no_domain(string? shown)
    {
        Assert.Equal("", BrowserSites.DomainOf(shown));
    }

    [Fact]
    public void Websites_are_grouped_across_computers_with_the_most_used_first()
    {
        var sites = BrowserSites.Group(
        [
            ("PC-02", "games.example.net", "Chrome"), ("PC-01", "games.example.net", "Edge"), ("PC-01", "Games.Example.net", "Edge"),
            ("PC-03", "school.example.org", "Firefox"),
        ]);

        Assert.Equal(["games.example.net", "school.example.org"], sites.Select(site => site.Domain));
        Assert.Equal(["PC-01", "PC-02"], sites[0].Computers);
        Assert.Equal(["Chrome", "Edge"], sites[0].Browsers);
        // The row the teacher ticks gives the domain back unchanged.
        Assert.Equal("games.example.net", BrowserSites.DomainOfRow(sites[0].Row));
        Assert.Contains("2 computer(s): PC-01, PC-02", sites[0].Row);
    }

    [Fact]
    public void An_answer_lists_only_windows_that_show_a_website()
    {
        var answer = new JsonObject
        {
            [BrowserSites.Sites] = new JsonArray(
                new JsonObject { [BrowserSites.Browser] = "Edge", [BrowserSites.Title] = "New tab", [BrowserSites.Domain] = "" },
                new JsonObject { [BrowserSites.Browser] = "Firefox", [BrowserSites.Title] = "Example", [BrowserSites.Domain] = "example.com" }),
        };

        Assert.Equal([("example.com", "Firefox")], BrowserSites.FromAnswer(answer));
        Assert.Empty(BrowserSites.FromAnswer(new JsonObject()));
    }

    [Theory]
    [InlineData(Cmd.BrowserPolicyGet, false)]
    [InlineData(Cmd.BrowserPolicySet, false)]      // machine-wide: the service writes it
    [InlineData(Cmd.BrowserDataClear, true)]       // the signed-in user's own files: the helper handles them
    [InlineData(Cmd.BrowserProfilesList, true)]
    [InlineData(Cmd.BrowserProfilesRemove, true)]
    public void Browser_management_is_a_protocol_1_10_administrator_feature(string command, bool userOnly)
    {
        Assert.Equal("1.10", Protocol.RequiredVersion(command));
        Assert.Equal(userOnly, Cmd.IsUserOnly(command));
        Assert.Contains(command, Protocol.Capabilities);
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(command));
        Assert.False(Protocol.SupportsCommand(command, "1.9", [], false));
    }

    [Fact]
    public void Each_policy_names_values_for_all_three_browsers_and_the_answers_read_as_sentences()
    {
        foreach (var policy in BrowserRules.Policies.Except(BrowserRules.TranslationPolicies))
            Assert.Equal(["Chrome", "Edge", "Firefox"], BrowserRules.Settings(policy).Select(setting => setting.Browser).Distinct());
        // Translation is chosen per browser: together the three policies cover each browser once.
        Assert.Equal(["Chrome", "Edge", "Firefox"], BrowserRules.TranslationPolicies.Select(policy => Assert.Single(BrowserRules.Settings(policy)).Browser));
        Assert.Empty(BrowserRules.Settings("something-else"));
        // Every value lives under the browser's own policy key, never anywhere else in the registry.
        Assert.All(BrowserRules.Policies.SelectMany(BrowserRules.Settings), setting => Assert.StartsWith(@"SOFTWARE\Policies\", setting.Key));

        Assert.Equal("12 file(s) removed, 3.5 MB freed; 1 file(s) could not be removed (still in use)", BrowserRules.DescribeCleared(
            new JsonObject { [BrowserRules.Files] = 12, [BrowserRules.Bytes] = 3670016, [BrowserRules.Failed] = 1 }));
        Assert.Equal("nothing to clear", BrowserRules.DescribeCleared(new JsonObject()));
        var removed = new JsonObject { [BrowserRules.Removed] = new JsonArray(new BrowserRules.Profile("Chrome", "Profile 1", "Games", "a@example.com", false).ToJson()) };
        Assert.Equal("1 profile(s) removed: Chrome \"Games\" (a@example.com)", BrowserRules.DescribeRemoved(removed));
        Assert.Equal("no extra profiles", BrowserRules.DescribeRemoved(new JsonObject()));
        Assert.Contains("No new browser profiles or guest windows: on", BrowserRules.DescribePolicies(
            new JsonObject { [BrowserRules.PreventProfiles] = "on", [BrowserRules.ClearOnExit] = "off" }));
    }

    [Fact]
    public void Reading_open_websites_is_a_classroom_status_of_the_signed_in_user()
    {
        Assert.Equal("1.10", Protocol.RequiredVersion(Cmd.BrowserSites));
        Assert.True(Cmd.IsUserOnly(Cmd.BrowserSites));
        Assert.Contains(Cmd.BrowserSites, Protocol.Capabilities);
        // Like the window titles a Teacher key already sees.
        Assert.True(CommandAccess.IsAllowed(OperatorRole.Teacher, Cmd.BrowserSites));
    }

    [Fact]
    public void Adding_to_the_website_block_is_a_protocol_1_10_request_of_the_service()
    {
        Assert.Equal(OperatorRole.Teacher, CommandAccess.RequiredRole(Cmd.BlockHostsAdd));
        Assert.Equal("1.10", Protocol.RequiredVersion(Cmd.BlockHostsAdd));
        Assert.False(Cmd.IsUserOnly(Cmd.BlockHostsAdd));       // the hosts file is the service's to write
        Assert.Contains(Cmd.BlockHostsAdd, Protocol.Capabilities);
        Assert.False(Protocol.SupportsCommand(Cmd.BlockHostsAdd, "1.9", [], false));
    }
}
