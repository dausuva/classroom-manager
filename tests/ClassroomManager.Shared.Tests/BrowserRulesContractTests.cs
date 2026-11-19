// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>
/// What each browser policy writes into the registry and how profiles and results are described (docs/QUALITY.md: the mutation test found these texts and names
/// unwatched). A policy name that is one letter wrong is not an error anywhere: the browser simply ignores it. So the exact names are the contract and are asserted.
/// </summary>
public sealed class BrowserRulesContractTests
{
    const string Chrome = @"SOFTWARE\Policies\Google\Chrome";
    const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";
    const string Firefox = @"SOFTWARE\Policies\Mozilla\Firefox";

    static string Describe(BrowserRules.Setting setting) =>
        $"{setting.Browser}|{setting.Key}|{setting.Name}|{setting.Number}|{(setting.List is null ? "" : string.Join(",", setting.List))}|{setting.Text}";

    [Fact]
    public void No_new_profiles_writes_these_policies_for_the_three_browsers()
    {
        Assert.Equal(
        [
            $"Chrome|{Chrome}|BrowserAddPersonEnabled|0||", $"Chrome|{Chrome}|BrowserGuestModeEnabled|0||",
            $"Edge|{Edge}|BrowserAddProfileEnabled|0||", $"Edge|{Edge}|BrowserGuestModeEnabled|0||",
            $"Firefox|{Firefox}|BlockAboutProfiles|1||",
        ], BrowserRules.Settings(BrowserRules.PreventProfiles).Select(Describe));
    }

    [Fact]
    public void Clearing_on_close_writes_these_policies_and_switches_Chrome_sync_off_with_it()
    {
        Assert.Equal(
        [
            $"Chrome|{Chrome}|SyncDisabled|1||",
            $"Chrome|{Chrome}|ClearBrowsingDataOnExitList|0|browsing_history,download_history,cookies_and_other_site_data,cached_images_and_files,autofill,hosted_app_data|",
            $"Edge|{Edge}|ClearBrowsingDataOnExit|1||", $"Firefox|{Firefox}|SanitizeOnShutdown|1||",
        ], BrowserRules.Settings(BrowserRules.ClearOnExit).Select(Describe));
    }

    [Fact]
    public void Page_translation_is_turned_off_per_browser_and_Firefox_locks_it_through_its_preferences()
    {
        Assert.Equal([$"Chrome|{Chrome}|TranslateEnabled|0||"], BrowserRules.Settings(BrowserRules.NoTranslateChrome).Select(Describe));
        Assert.Equal([$"Edge|{Edge}|TranslateEnabled|0||"], BrowserRules.Settings(BrowserRules.NoTranslateEdge).Select(Describe));
        var firefox = Assert.Single(BrowserRules.Settings(BrowserRules.NoTranslateFirefox));
        Assert.Equal(($"Firefox", Firefox, "Preferences"), (firefox.Browser, firefox.Key, firefox.Name));
        Assert.Equal("""{"browser.translations.enable":{"Value":false,"Status":"locked"}}""", firefox.Text);
    }

    [Fact]
    public void Secure_DNS_is_kept_off_in_all_three_browsers_while_a_website_is_blocked_and_an_unknown_policy_writes_nothing()
    {
        Assert.Equal(
        [
            $"Chrome|{Chrome}|DnsOverHttpsMode|0||off", $"Edge|{Edge}|DnsOverHttpsMode|0||off",
            $@"Firefox|{Firefox}\DNSOverHTTPS|Enabled|0||", $@"Firefox|{Firefox}\DNSOverHTTPS|Locked|1||",
        ], BrowserRules.Settings(BrowserRules.NoSecureDns).Select(Describe));
        Assert.Empty(BrowserRules.Settings("somethingElse"));
        Assert.Empty(BrowserRules.Settings(""));
    }

    [Fact]
    public void The_policy_names_in_the_requests_and_the_texts_shown_for_them()
    {
        Assert.Equal(["preventProfiles", "clearOnExit", "noTranslateChrome", "noTranslateEdge", "noTranslateFirefox"], BrowserRules.Policies);
        Assert.Equal(["noTranslateChrome", "noTranslateEdge", "noTranslateFirefox"], BrowserRules.TranslationPolicies);
        Assert.Equal("forUser", BrowserRules.ForUser);
        Assert.Equal("noSecureDns", BrowserRules.NoSecureDns);
        Assert.Equal("No new browser profiles or guest windows", BrowserRules.PolicyName(BrowserRules.PreventProfiles));
        Assert.Equal("Clear browsing data when the browser closes", BrowserRules.PolicyName(BrowserRules.ClearOnExit));
        Assert.Equal("No page translation in Chrome", BrowserRules.PolicyName(BrowserRules.NoTranslateChrome));
        Assert.Equal("No page translation in Edge", BrowserRules.PolicyName(BrowserRules.NoTranslateEdge));
        Assert.Equal("No page translation in Firefox", BrowserRules.PolicyName(BrowserRules.NoTranslateFirefox));
        Assert.Equal("anythingElse", BrowserRules.PolicyName("anythingElse"));
        Assert.Equal("Google Chrome", BrowserRules.TranslationBrowser(BrowserRules.NoTranslateChrome));
        Assert.Equal("Microsoft Edge", BrowserRules.TranslationBrowser(BrowserRules.NoTranslateEdge));
        Assert.Equal("Mozilla Firefox", BrowserRules.TranslationBrowser(BrowserRules.NoTranslateFirefox));
        Assert.Equal("anythingElse", BrowserRules.TranslationBrowser("anythingElse"));
    }

    [Fact]
    public void A_profile_goes_over_the_wire_under_these_names_and_a_missing_part_reads_as_empty()
    {
        var profile = new BrowserRules.Profile("Chrome", "Profile 1", "Anna", "anna@school.example", false);
        var json = profile.ToJson();
        Assert.Equal(["account", "browser", "first", "id", "name"], json.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(profile, BrowserRules.Profile.FromJson(json));
        Assert.True(BrowserRules.Profile.FromJson(new BrowserRules.Profile("Edge", "Default", "Person 1", "", true).ToJson()).IsFirst);
        Assert.Equal(new BrowserRules.Profile("", "", "", "", false), BrowserRules.Profile.FromJson(null));
        Assert.Equal(new BrowserRules.Profile("", "", "", "", false), BrowserRules.Profile.FromJson(new JsonObject()));
        Assert.False(BrowserRules.Profile.FromJson(new JsonObject { ["first"] = false }).IsFirst);
    }

    [Fact]
    public void A_profile_is_named_by_its_browser_and_folder_and_shown_with_its_account_only_when_it_has_one()
    {
        Assert.Equal("Chrome|Profile 1", new BrowserRules.Profile("Chrome", "Profile 1", "Anna", "a@x.example", false).Key);
        Assert.Equal("Chrome \"Anna\" (a@x.example)", new BrowserRules.Profile("Chrome", "Profile 1", "Anna", "a@x.example", false).ToString());
        Assert.Equal("Edge \"Person 1\"", new BrowserRules.Profile("Edge", "Default", "Person 1", "", true).ToString());
    }

    [Fact]
    public void The_profiles_of_a_reply_are_read_in_order_and_a_reply_without_them_has_none()
    {
        var data = new JsonObject
        {
            [BrowserRules.Profiles] = new JsonArray(
                new BrowserRules.Profile("Chrome", "Default", "A", "", true).ToJson(), new BrowserRules.Profile("Chrome", "Profile 1", "B", "", false).ToJson()),
        };
        Assert.Equal(["A", "B"], BrowserRules.ProfilesFrom(data).Select(profile => profile.Name));
        Assert.Empty(BrowserRules.ProfilesFrom(new JsonObject()));
        Assert.Equal("ids", BrowserRules.ProfileKeys);
        Assert.Equal("profiles", BrowserRules.Profiles);
    }

    [Theory]
    [InlineData("1.16", true)]
    [InlineData("1.19", true)]
    [InlineData("1.15", false)]
    [InlineData(null, false)]
    public void Removing_chosen_profiles_needs_a_Client_from_protocol_1_16(string? version, bool can) => Assert.Equal(can, BrowserRules.CanRemoveSelected(version));

    [Fact]
    public void What_was_cleared_removed_and_set_is_described_in_words()
    {
        Assert.Equal("nothing to clear", BrowserRules.DescribeCleared(new JsonObject()));
        Assert.Equal("3 file(s) removed, 2.5 MB freed",
            BrowserRules.DescribeCleared(new JsonObject { [BrowserRules.Files] = 3, [BrowserRules.Bytes] = 2621440, [BrowserRules.Failed] = 0 }));
        Assert.Equal("3 file(s) removed, 0 MB freed; 2 file(s) could not be removed (still in use)",
            BrowserRules.DescribeCleared(new JsonObject { [BrowserRules.Files] = 3, [BrowserRules.Bytes] = 10, [BrowserRules.Failed] = 2 }));
        Assert.Equal("nothing to clear; 1 file(s) could not be removed (still in use)", BrowserRules.DescribeCleared(new JsonObject { [BrowserRules.Failed] = 1 }));

        Assert.Equal("no extra profiles", BrowserRules.DescribeRemoved(new JsonObject()));
        Assert.Equal("2 profile(s) removed: Chrome \"B\", Edge \"C\" (c@x.example)",
            BrowserRules.DescribeRemoved(new JsonObject
            {
                [BrowserRules.Removed] = new JsonArray(new BrowserRules.Profile("Chrome", "P1", "B", "", false).ToJson(), new BrowserRules.Profile("Edge", "P2", "C", "c@x.example", false).ToJson()),
            }));

        Assert.Equal("No new browser profiles or guest windows: on; Clear browsing data when the browser closes: off; No page translation in Chrome: unknown; No page translation in Edge: unknown; No page translation in Firefox: partly",
            BrowserRules.DescribePolicies(new JsonObject { [BrowserRules.PreventProfiles] = "on", [BrowserRules.ClearOnExit] = "off", [BrowserRules.NoTranslateFirefox] = "partly" }));
    }
}
