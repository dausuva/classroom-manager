// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-177: the selection buttons above the computer list.</summary>
public sealed class ComputerSelectionTests
{
    static ClientInfo Computer(string name, string status, bool selected = false) =>
        new() { Name = name, Address = "10.0.0." + name.Length, Status = status, IsSelected = selected };

    static List<ClientInfo> Room() =>
    [
        Computer("A1", "Online"),
        Computer("A2", "Online - nobody signed in"),
        Computer("A3", "Offline (no reply)"),
        Computer("A4", "Offline: The operation timed out"),
        Computer("A5", "Discovered - assign to a room to monitor"),
        Computer("A6", "Checking..."),
    ];

    static string[] Selected(IEnumerable<ClientInfo> room) => room.Where(c => c.IsSelected).Select(c => c.Name).ToArray();

    [Fact]
    public void Online_means_the_computer_answered_even_when_nobody_is_signed_in()
    {
        var room = Room();
        Assert.Equal(["A1", "A2"], room.Where(ComputerSelection.IsOnline).Select(c => c.Name));
    }

    [Fact]
    public void Offline_means_no_answer_and_leaves_out_computers_that_are_only_discovered_or_busy()
    {
        var room = Room();
        Assert.Equal(["A3", "A4"], room.Where(ComputerSelection.IsOffline).Select(c => c.Name));
    }

    [Fact]
    public void Offline_selection_replaces_the_one_before()
    {
        var room = Room();
        room[0].IsSelected = true;

        ComputerSelection.Only(room, room, ComputerSelection.IsOffline);

        Assert.Equal(["A3", "A4"], Selected(room));
    }

    [Fact]
    public void A_computer_the_list_does_not_show_is_cleared_by_online_and_offline_but_untouched_by_invert()
    {
        var room = Room();
        var hidden = Computer("Z9", "Offline (no reply)", selected: true);
        var all = room.Append(hidden).ToList();

        ComputerSelection.Invert(room);
        Assert.True(hidden.IsSelected);

        ComputerSelection.Only(room, all, ComputerSelection.IsOffline);
        Assert.False(hidden.IsSelected);
    }

    [Fact]
    public void Invert_swaps_every_shown_computer()
    {
        var room = Room();
        room[0].IsSelected = true;
        room[3].IsSelected = true;

        ComputerSelection.Invert(room);

        Assert.Equal(["A2", "A3", "A5", "A6"], Selected(room));
    }

    [Fact]
    public void Invert_twice_gives_the_selection_back_and_an_empty_list_is_no_problem()
    {
        var room = Room();
        room[1].IsSelected = true;
        room[4].IsSelected = true;

        ComputerSelection.Invert(room);
        ComputerSelection.Invert(room);

        Assert.Equal(["A2", "A5"], Selected(room));
        ComputerSelection.Invert([]);
        ComputerSelection.Only([], [], ComputerSelection.IsOnline);
    }
}
