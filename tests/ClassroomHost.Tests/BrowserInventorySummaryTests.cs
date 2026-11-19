// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.BrowserInventory;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class BrowserInventorySummaryTests
{
    static BrowserSnapshot Answer(string defaultId, params (string Id, string Name)[] browsers) =>
        new(browsers.Select(browser => new BrowserIdentity(browser.Id, browser.Name)).ToArray(), defaultId, "");

    [Fact]
    public void The_summary_names_where_a_main_browser_is_missing_and_what_opens_links()
    {
        var text = BrowserInventorySummary.Describe(
        [
            ("PC-01", Answer("chrome", ("chrome", "Google Chrome"), ("edge", "Microsoft Edge"))),
            ("PC-02", Answer("edge", ("edge", "Microsoft Edge"), ("brave", "Brave"))),
            ("PC-03", null),
        ]);

        Assert.Contains("Browsers read from 2 of 3 computer(s):", text);
        Assert.Contains("• Google Chrome: on 1 of 2 computers; missing on PC-02", text);
        Assert.Contains("• Microsoft Edge: on all 2 computers", text);
        Assert.Contains("• Mozilla Firefox: on 0 of 2 computers; missing on PC-01, PC-02", text);
        Assert.Contains("• Also: Brave (1)", text);
        Assert.Contains("Opens web links for the signed-in user: Google Chrome (1), Microsoft Edge (1)", text);
    }

    [Fact]
    public void The_default_can_be_any_main_browser_or_another_one_found()
    {
        var choices = BrowserInventorySummary.DefaultChoices([Answer("", ("vivaldi", "Vivaldi")), null]);
        Assert.Equal(["chrome", "edge", "firefox", "vivaldi"], choices.Select(choice => choice.Id));
    }

    [Fact]
    public void Unread_inventory_does_not_assume_every_failure_is_an_old_client() =>
        Assert.Contains("per-computer read status", BrowserInventorySummary.Describe([("PC-01", null)]));
}
