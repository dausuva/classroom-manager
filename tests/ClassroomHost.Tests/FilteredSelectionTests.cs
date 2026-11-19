// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class FilteredSelectionTests
{
    static readonly string[] Apps = ["chrome.exe", "Discord.exe", "notepad.exe", "steam.exe"];

    [Fact]
    public void Filter_matches_any_part_of_the_name_ignoring_case_and_padding()
    {
        var selection = new FilteredSelection(Apps);

        Assert.Equal(["chrome.exe", "Discord.exe"], selection.Visible("  C "));
        Assert.Equal(["Discord.exe"], selection.Visible("disc"));
        Assert.Empty(selection.Visible("zoom"));
        Assert.Equal(Apps, selection.Visible(""));
    }

    [Fact]
    public void Choices_hidden_by_the_filter_are_kept()
    {
        var selection = new FilteredSelection(Apps, ["steam.exe"]);

        // The user narrows the list to entries that do not include steam.exe, then ticks one of them.
        Assert.DoesNotContain("steam.exe", selection.Visible("note"));
        selection.Choose(["notepad.exe"]);

        Assert.Equal(["notepad.exe", "steam.exe"], selection.Chosen);
        Assert.Equal(2, selection.ChosenCount);
    }

    [Fact]
    public void Initial_choices_match_ignoring_case_and_ignore_names_not_in_the_list()
    {
        var selection = new FilteredSelection(Apps, ["DISCORD.EXE", "missing.exe"]);

        Assert.Equal(["Discord.exe"], selection.Chosen);
        Assert.True(selection.IsChosen("discord.exe"));
    }

    [Fact]
    public void Unchoosing_removes_only_the_named_entries()
    {
        var selection = new FilteredSelection(Apps, Apps);

        selection.Unchoose(["chrome.exe"]);

        Assert.Equal(["Discord.exe", "notepad.exe", "steam.exe"], selection.Chosen);
        Assert.Equal(4, selection.TotalCount);
    }
}
