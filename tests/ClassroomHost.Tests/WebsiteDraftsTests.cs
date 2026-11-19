// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-162: websites noted to block later stay in the Host and are tidy.</summary>
public sealed class WebsiteDraftsTests
{
    static readonly DateTime Now = new(2026, 10, 23, 9, 30, 0);

    [Fact]
    public void Websites_are_noted_once_in_the_form_the_block_list_uses()
    {
        var drafts = new List<WebsiteDraft>();

        var added = WebsiteDrafts.Add(drafts, ["https://www.Example.com/page", "example.com", ".games.example.net"], WebsiteDrafts.SourceInUse, Now);

        Assert.Equal(2, added);
        Assert.Equal(["example.com", "games.example.net"], drafts.Select(d => d.Domain));
        Assert.All(drafts, d => Assert.Equal(Now, d.Added));
    }

    [Fact]
    public void Text_that_is_not_a_website_is_not_noted()
    {
        var drafts = new List<WebsiteDraft>();

        Assert.Equal(0, WebsiteDrafts.Add(drafts, ["", "  ", "two words", "about:blank", "10.0.0.5"], "x", Now));
        Assert.Empty(drafts);
    }

    [Fact]
    public void A_single_word_intranet_name_is_kept_as_typed()
    {
        var drafts = new List<WebsiteDraft>();

        WebsiteDrafts.Add(drafts, ["Intranet"], "x", Now);

        Assert.Equal("intranet", Assert.Single(drafts).Domain);
    }

    [Fact]
    public void A_website_noted_twice_stays_one_entry_with_its_first_day()
    {
        var drafts = new List<WebsiteDraft>();
        WebsiteDrafts.Add(drafts, ["example.com"], "first", Now);

        Assert.Equal(0, WebsiteDrafts.Add(drafts, ["EXAMPLE.com"], "second", Now.AddDays(3)));

        var only = Assert.Single(drafts);
        Assert.Equal("first", only.Source);
        Assert.Equal(Now, only.Added);
    }

    [Fact]
    public void Removing_takes_only_the_named_ones()
    {
        var drafts = new List<WebsiteDraft>();
        WebsiteDrafts.Add(drafts, ["a.com", "b.com", "c.com"], "x", Now);

        Assert.Equal(2, WebsiteDrafts.Remove(drafts, ["A.com", "c.com", "never.com"]));

        Assert.Equal(["b.com"], drafts.Select(d => d.Domain));
    }

    [Fact]
    public void A_row_gives_the_domain_back()
    {
        var draft = new WebsiteDraft { Domain = "example.com", Added = Now, Source = "x" };

        Assert.Equal("example.com", WebsiteDrafts.DomainOfRow(WebsiteDrafts.Row(draft)));
        Assert.Equal("example.com", WebsiteDrafts.DomainOfRow(WebsiteDrafts.Row(new WebsiteDraft { Domain = "example.com" })));
    }

    [Fact]
    public void A_saved_list_is_cleaned_when_settings_are_read()
    {
        var messy = new List<WebsiteDraft>
        {
            new() { Domain = "Example.com", Added = Now, Source = "x" },
            new() { Domain = "example.com", Added = Now.AddDays(1), Source = "y" },
            new() { Domain = "  ", Added = Now, Source = "z" },
        };

        var clean = WebsiteDrafts.Normalize(messy);

        Assert.Equal("example.com", Assert.Single(clean).Domain);
        Assert.Empty(WebsiteDrafts.Normalize(null));
    }

    [Fact]
    public void Drafts_are_kept_with_the_settings_not_sent_anywhere()
    {
        var settings = new HostSettings();
        WebsiteDrafts.Add(settings.DraftWebsites, ["example.com"], "x", Now);

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = System.Text.Json.JsonSerializer.Deserialize<HostSettings>(json)!;

        Assert.Equal("example.com", Assert.Single(back.DraftWebsites).Domain);
    }
}
