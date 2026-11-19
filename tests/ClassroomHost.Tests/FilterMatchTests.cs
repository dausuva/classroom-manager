// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-163: the search boxes of the program and process lists.</summary>
public sealed class FilterMatchTests
{
    // What the Programs window holds for Microsoft Edge, as the Client reads it from the registry.
    const string EdgeName = "Microsoft Edge";
    const string EdgePublisher = "Microsoft Corporation";
    const string EdgeFolder = @"C:\Program Files (x86)\Microsoft\Edge\Application";
    const string EdgeIcon = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe,0";

    static bool Program(string filter) => FilterMatch.Matches(filter, EdgeName, EdgePublisher, EdgeFolder, EdgeIcon);

    [Theory]
    [InlineData("msedge")]
    [InlineData("msedge.exe")]
    [InlineData("edge")]
    [InlineData("microsoft edge")]
    [InlineData("edge microsoft")]
    [InlineData("corporation edge")]
    [InlineData("MSEDGE")]
    [InlineData("application edge")]
    public void A_program_is_found_by_its_process_name_name_publisher_or_folder(string filter) => Assert.True(Program(filter));

    [Theory]
    [InlineData("chrome")]
    [InlineData("edge zoom")]
    [InlineData("msedge2")]
    public void Every_word_must_be_found_so_a_wrong_word_finds_nothing(string filter) => Assert.False(Program(filter));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_filter_matches_everything(string? filter) => Assert.True(FilterMatch.Matches(filter, "anything"));

    [Fact]
    public void Case_and_accents_do_not_matter()
    {
        Assert.True(FilterMatch.Matches("zymeti", "Žymėti visus"));
        Assert.True(FilterMatch.Matches("ŽYMĖTI", "zymeti visus"));
        Assert.True(FilterMatch.Matches("klase", "Klasės raktas"));
        Assert.Equal("zymeti", FilterMatch.Fold("Žymėti"));
    }

    [Fact]
    public void Empty_fields_are_skipped_and_all_empty_fields_match_only_an_empty_filter()
    {
        Assert.True(FilterMatch.Matches("zoom", null, "", "Zoom Workplace"));
        Assert.False(FilterMatch.Matches("zoom", null, "", null));
        Assert.True(FilterMatch.Matches("", null, ""));
    }

    [Fact]
    public void A_process_is_found_by_its_path_owner_or_window_title_in_any_order()
    {
        const string name = "msedge", title = "Docs - Microsoft Edge", owner = @"CLASS\student7", kind = "User / windowed app";
        const string path = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

        Assert.True(FilterMatch.Matches("student7 edge", name, title, owner, kind, path));
        Assert.True(FilterMatch.Matches("windowed docs", name, title, owner, kind, path));
        Assert.True(FilterMatch.Matches("application msedge.exe", name, title, owner, kind, path));
        Assert.False(FilterMatch.Matches("student8", name, title, owner, kind, path));
    }

    [Fact]
    public void A_word_may_appear_in_different_fields()
    {
        // "microsoft" is in the publisher and "msedge" only in the file: both must be found, in any field.
        Assert.True(FilterMatch.Matches("microsoft msedge", "Edge", "Microsoft Corporation", @"C:\x\msedge.exe"));
    }
}
