// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// What the Block websites, Websites in use and Block apps dialogs ask for, decided without a window (T-253 phase 2). These are the rules behind the three
/// restriction dialogs: what is typed becomes which list, which computers get what, and what the teacher is told before it runs.
/// </summary>
public sealed class BlockingPlansTests
{
    // ---- Block websites ---------------------------------------------------------------------

    [Fact]
    public void Typed_domains_are_split_on_commas_and_lines_trimmed_lower_cased_without_a_leading_dot_and_each_once()
    {
        Assert.Equal(["example.com", "games.example.net", "a.org"], WebsiteBlockPlan.ParseDomains(" Example.COM,\n.games.example.net\r\n\r\n, example.com ,A.org"));
        Assert.Empty(WebsiteBlockPlan.ParseDomains(" , \n "));
        Assert.Empty(WebsiteBlockPlan.ParseDomains(""));
    }

    [Fact]
    public void The_five_actions_are_listed_in_the_order_the_dialog_shows_them_and_the_safe_one_is_chosen_for_several_computers_or_a_draft()
    {
        Assert.Equal(5, WebsiteBlockPlan.Modes.Length);
        Assert.Equal(WebsiteBlockPlan.Merge, WebsiteBlockPlan.Modes[0]);
        Assert.Equal(WebsiteBlockPlan.Replace, WebsiteBlockPlan.Modes[1]);
        Assert.Equal(WebsiteBlockPlan.Draft, WebsiteBlockPlan.Modes[4]);
        Assert.Equal(0, WebsiteBlockPlan.DefaultModeIndex(hasSuggested: true, targetCount: 1));
        Assert.Equal(0, WebsiteBlockPlan.DefaultModeIndex(false, 2));
        Assert.Equal(1, WebsiteBlockPlan.DefaultModeIndex(false, 1));
    }

    [Fact]
    public void The_draft_domains_are_added_to_what_one_computer_blocks_now_each_once()
    {
        Assert.Equal("a.com\nb.com\nc.com", WebsiteBlockPlan.Prefill("a.com\nb.com\n", ["B.com", "c.com"]));
        Assert.Equal("a.com\nb.com", WebsiteBlockPlan.Prefill("a.com\nb.com", null));
        Assert.Equal("a.com\nb.com", WebsiteBlockPlan.Prefill("a.com\nb.com", []));
        Assert.Equal("c.com", WebsiteBlockPlan.Prefill("", ["c.com"]));
    }

    [Fact]
    public void An_empty_list_is_a_problem_unless_the_block_is_being_cleared()
    {
        Assert.Contains("Enter at least one domain", WebsiteBlockPlan.Build(WebsiteBlockPlan.Merge, " ").Problem);
        Assert.Contains("Enter at least one domain", WebsiteBlockPlan.Build(WebsiteBlockPlan.Draft, "").Problem);
        Assert.Null(WebsiteBlockPlan.Build(WebsiteBlockPlan.Clear, "").Problem);
        Assert.Null(WebsiteBlockPlan.Build(WebsiteBlockPlan.Merge, "a.com").Problem);
        Assert.True(WebsiteBlockPlan.Build(WebsiteBlockPlan.Draft, "a.com").IsDraft);
        Assert.False(WebsiteBlockPlan.Build(WebsiteBlockPlan.Merge, "a.com").IsDraft);
    }

    [Fact]
    public void Each_action_has_its_title_and_only_replacing_or_clearing_earns_the_warning()
    {
        Assert.Equal("Clear website block", WebsiteBlockPlan.Build(WebsiteBlockPlan.Clear, "").Title);
        Assert.Equal("Unblock 2 website(s)", WebsiteBlockPlan.Build(WebsiteBlockPlan.Remove, "a.com, b.com").Title);
        Assert.Equal("Replace block with 1 website(s)", WebsiteBlockPlan.Build(WebsiteBlockPlan.Replace, "a.com").Title);
        Assert.Equal("Add 3 website(s) to the block", WebsiteBlockPlan.Build(WebsiteBlockPlan.Merge, "a.com, b.com, c.com").Title);
        Assert.Contains("replaces or clears", WebsiteBlockPlan.Build(WebsiteBlockPlan.Replace, "a.com").Warning);
        Assert.Contains("replaces or clears", WebsiteBlockPlan.Build(WebsiteBlockPlan.Clear, "").Warning);
        Assert.Equal("", WebsiteBlockPlan.Build(WebsiteBlockPlan.Merge, "a.com").Warning);
        Assert.Equal("", WebsiteBlockPlan.Build(WebsiteBlockPlan.Remove, "a.com").Warning);
    }

    [Fact]
    public void The_review_names_the_action_and_the_domains_or_the_whole_managed_block()
    {
        var merge = WebsiteBlockPlan.Build(WebsiteBlockPlan.Merge, "a.com, b.com").ReviewItems.Select(item => (item.Label, item.Value)).ToList();
        Assert.Equal([("Action", WebsiteBlockPlan.Merge), ("Domains", "a.com, b.com")], merge);
        var clear = WebsiteBlockPlan.Build(WebsiteBlockPlan.Clear, "").ReviewItems.Select(item => (item.Label, item.Value)).ToList();
        Assert.Equal([("Action", WebsiteBlockPlan.Clear), ("Domains", "(entire managed block)")], clear);
    }

    [Fact]
    public void The_add_request_is_used_only_to_merge_plain_website_names_on_a_Client_that_knows_it()
    {
        var plain = WebsiteBlockPlan.Build(WebsiteBlockPlan.Merge, "example.com, games.example.net");
        Assert.True(plain.UsesAddRequest(clientSupportsAdd: true));
        Assert.False(plain.UsesAddRequest(clientSupportsAdd: false));
        Assert.False(WebsiteBlockPlan.Build(WebsiteBlockPlan.Merge, "example.com, intranet").UsesAddRequest(true));   // a single-word name goes the read-and-replace way
        Assert.False(WebsiteBlockPlan.Build(WebsiteBlockPlan.Remove, "example.com").UsesAddRequest(true));
        Assert.False(WebsiteBlockPlan.Build(WebsiteBlockPlan.Replace, "example.com").UsesAddRequest(true));
    }

    [Fact]
    public void Merging_keeps_what_a_computer_has_and_adds_the_new_and_removing_takes_only_the_named_ones_away()
    {
        string[] existing = ["old.com", "Keep.org", "b.com"];
        Assert.Equal(["old.com", "Keep.org", "b.com", "new.net"], WebsiteBlockPlan.Build(WebsiteBlockPlan.Merge, "B.com, new.net").ListAfter(existing));
        Assert.Equal(["old.com", "Keep.org"], WebsiteBlockPlan.Build(WebsiteBlockPlan.Remove, "b.com, absent.io").ListAfter(existing));
        Assert.Equal(existing, WebsiteBlockPlan.Build(WebsiteBlockPlan.Remove, "absent.io").ListAfter(existing));
    }

    // ---- Websites in use --------------------------------------------------------------------

    static (ClientInfo A, ClientInfo B, ClientInfo C, Dictionary<ClientInfo, HashSet<string>> OpenOn) Room()
    {
        var a = new ClientInfo { Name = "PC-A" };
        var b = new ClientInfo { Name = "PC-B" };
        var c = new ClientInfo { Name = "PC-C" };
        var open = new Dictionary<ClientInfo, HashSet<string>>
        {
            [a] = new(["games.example", "news.example"], StringComparer.OrdinalIgnoreCase),
            [b] = new(["news.example"], StringComparer.OrdinalIgnoreCase),
        };
        return (a, b, c, open);
    }

    [Fact]
    public void Websites_in_use_blocks_the_ticked_ones_or_only_notes_them_or_notes_all_that_are_open()
    {
        var (_, _, _, open) = Room();
        Assert.Equal(SitesInUseAction.Block, SitesInUsePlan.Build(SitesInUsePlan.BlockNow, SitesInUsePlan.Everywhere, "", open).Action);
        Assert.Equal(SitesInUseAction.DraftTicked, SitesInUsePlan.Build(SitesInUsePlan.DraftTicked, SitesInUsePlan.Everywhere, "", open).Action);
        Assert.Equal(SitesInUseAction.DraftAll, SitesInUsePlan.Build(SitesInUsePlan.DraftAll, SitesInUsePlan.Everywhere, "", open).Action);
    }

    [Fact]
    public void Nothing_ticked_is_a_problem_except_when_every_open_website_is_noted()
    {
        var (_, _, _, open) = Room();
        Assert.Contains("Tick at least one", SitesInUsePlan.Build(SitesInUsePlan.BlockNow, SitesInUsePlan.Everywhere, "", open).Problem);
        Assert.Contains("Tick at least one", SitesInUsePlan.Build(SitesInUsePlan.DraftTicked, SitesInUsePlan.Everywhere, "\n", open).Problem);
        Assert.Null(SitesInUsePlan.Build(SitesInUsePlan.DraftAll, SitesInUsePlan.Everywhere, "", open).Problem);
    }

    [Fact]
    public void The_ticked_rows_become_domains_once_each_and_only_the_computers_where_one_is_open_are_chosen_when_asked()
    {
        var (a, b, c, open) = Room();
        var ticked = SitesInUsePlan.Build(SitesInUsePlan.BlockNow, SitesInUsePlan.WhereOpen, "games.example\nGAMES.example", open);
        Assert.Single(ticked.Blocked);
        Assert.Equal([a], ticked.Targets([a, b, c]));
        Assert.Equal(["games.example"], ticked.AddFor(a));

        var news = SitesInUsePlan.Build(SitesInUsePlan.BlockNow, SitesInUsePlan.WhereOpen, "games.example\nnews.example", open);
        Assert.Equal([a, b], news.Targets([a, b, c]));
        Assert.Equal(["games.example", "news.example"], news.AddFor(a));
        Assert.Equal(["news.example"], news.AddFor(b));

        var everywhere = SitesInUsePlan.Build(SitesInUsePlan.BlockNow, SitesInUsePlan.Everywhere, "games.example", open);
        Assert.Equal([a, b, c], everywhere.Targets([a, b, c]));
        Assert.Equal(["games.example"], everywhere.AddFor(c));   // a computer that did not answer still gets it when asked for everywhere
    }

    [Fact]
    public void Websites_in_use_has_its_title_and_review_lines()
    {
        var (_, _, _, open) = Room();
        var plan = SitesInUsePlan.Build(SitesInUsePlan.BlockNow, SitesInUsePlan.WhereOpen, "games.example\nnews.example", open);
        Assert.Equal("Block 2 website(s) in use", plan.Title);
        Assert.Equal(
        [
            ("Action", "Add to the existing website block"), ("Domains", "games.example, news.example"),
            ("Where", "Each domain only on the computers where it is open"),
        ], plan.ReviewItems.Select(item => (item.Label, item.Value)));
        Assert.Equal("Every domain on every selected computer",
            SitesInUsePlan.Build(SitesInUsePlan.BlockNow, SitesInUsePlan.Everywhere, "games.example", open).ReviewItems[2].Value);
    }

    // ---- Block apps -------------------------------------------------------------------------

    [Theory]
    [InlineData("game", "game.exe")]
    [InlineData("Game.EXE", "Game.EXE")]
    [InlineData("\"game\"", "game.exe")]
    [InlineData("  game  ", "game.exe")]
    [InlineData(@"C:\Games\game", @"C:\Games\game")]
    [InlineData("/opt/game", "/opt/game")]
    [InlineData("", "")]
    [InlineData("\"\"", "")]
    [InlineData("   ", "")]
    public void A_process_name_gets_exe_unless_it_is_a_path_or_has_it(string typed, string expected) => Assert.Equal(expected, AppBlockPlan.NormalizeEntry(typed));

    [Fact]
    public void The_names_come_from_what_was_ticked_what_was_typed_and_optionally_the_Host_defaults_each_once_in_any_case()
    {
        string[] catalog = ["steam", "Discord.exe"];
        var plan = AppBlockPlan.Build(false, "game.exe\nsteam", "Fortnite, \"minecraft\"\nGAME", true, catalog, false, false);
        Assert.Equal(["game.exe", "steam.exe", "Fortnite.exe", "minecraft.exe", "Discord.exe"], plan.Names);
        var without = AppBlockPlan.Build(false, "game.exe", "", false, catalog, false, false);
        Assert.Equal(["game.exe"], without.Names);
    }

    [Fact]
    public void Nothing_to_block_is_a_problem_unless_the_block_is_being_cleared()
    {
        Assert.Contains("Enter at least one process name", AppBlockPlan.Build(false, "", " , ", false, [], false, false).Problem);
        Assert.Null(AppBlockPlan.Build(true, "", "", false, [], false, false).Problem);
        Assert.Null(AppBlockPlan.Build(false, "", "game", false, [], false, false).Problem);
    }

    [Fact]
    public void The_title_and_the_review_say_whether_the_block_is_replaced_or_cleared_and_what_happens_to_the_defaults()
    {
        var apply = AppBlockPlan.Build(false, "a, b", "", false, [], true, false);
        Assert.Equal("Block 2 app(s)", apply.Title);
        Assert.Equal(
        [
            ("Action", "Replace the permanent app block"), ("Process names", "a.exe, b.exe"), ("Host defaults", "Add these names"),
        ], apply.ReviewItems.Select(item => (item.Label, item.Value)));
        Assert.Equal("Replace with these names", AppBlockPlan.Build(false, "a", "", false, [], true, true).ReviewItems[2].Value);
        Assert.Equal("Do not change", AppBlockPlan.Build(false, "a", "", false, [], false, true).ReviewItems[2].Value);

        var clear = AppBlockPlan.Build(true, "a", "", false, [], true, true);
        Assert.Equal("Clear app block", clear.Title);
        Assert.Equal(
        [
            ("Action", "Clear the permanent app block"), ("Process names", "(all blocked names)"), ("Host defaults", "Do not change"),
        ], clear.ReviewItems.Select(item => (item.Label, item.Value)));
        Assert.Contains("complete permanent app block", apply.Warning);
    }

    [Fact]
    public void The_Host_default_list_is_extended_replaced_or_left_alone_sorted_and_without_repeats()
    {
        string[] current = ["zoom.exe", "Chrome.exe"];
        Assert.Equal(["chrome.exe", "game.exe", "zoom.exe"],
            AppBlockPlan.Build(false, "game, chrome", "", false, [], true, false).CatalogAfter(current)!.Select(name => name.ToLowerInvariant()).ToList());
        Assert.Equal(["game.exe"], AppBlockPlan.Build(false, "game", "", false, [], true, true).CatalogAfter(current));
        Assert.Null(AppBlockPlan.Build(false, "game", "", false, [], false, false).CatalogAfter(current));
        Assert.Null(AppBlockPlan.Build(true, "game", "", false, [], true, true).CatalogAfter(current));
        Assert.Equal(["Chrome.exe", "game.exe", "zoom.exe"], AppBlockPlan.Build(false, "game", "", false, [], true, false).CatalogAfter(current));
    }
}
