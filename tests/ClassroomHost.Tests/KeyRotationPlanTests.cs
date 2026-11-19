// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class KeyRotationPlanTests
{
    static ClientInfo Pc(string name, string keyName = "") => new() { Name = name, KeyName = keyName };

    [Fact]
    public void The_choices_are_the_network_then_the_room_when_one_is_open_then_the_selection()
    {
        List<ClientInfo> network = [Pc("a"), Pc("b"), Pc("c")], inView = [Pc("a")], picked = [Pc("b"), Pc("c")];

        var noRoom = KeyRotationPlan.Choices(network, "", inView, picked);
        Assert.Equal(["Every saved computer on the selected network (3)", "Selected computers (2)"], noRoom.Select(c => c.Label));
        Assert.Same(network, noRoom[0].Targets);
        Assert.Same(picked, noRoom[1].Targets);

        var room = KeyRotationPlan.Choices(network, "Lab 2", inView, picked);
        Assert.Equal(["Every saved computer on the selected network (3)", "Every computer in room Lab 2 (1)", "Selected computers (2)"], room.Select(c => c.Label));
        Assert.Same(inView, room[1].Targets);
    }

    [Theory]
    [InlineData(0, "", 2, 0)]
    [InlineData(1, "", 2, 1)]
    [InlineData(1, "Lab 2", 3, 0)]    // a room is open: every saved computer is the first offer, not the selection
    [InlineData(0, "Lab 2", 3, 0)]
    public void The_selection_is_offered_when_there_is_one_and_no_room_is_open(int picked, string room, int choices, int expected) =>
        Assert.Equal(expected, KeyRotationPlan.DefaultChoice(picked, room, choices));

    [Fact]
    public void The_picked_label_finds_its_choice_and_a_label_not_found_means_the_first()
    {
        var choices = KeyRotationPlan.Choices([Pc("a")], "R", [Pc("v")], [Pc("p")]);
        Assert.Same(choices[2], KeyRotationPlan.Pick(choices, choices[2].Label));
        Assert.Same(choices[1], KeyRotationPlan.Pick(choices, choices[1].Label));
        Assert.Same(choices[0], KeyRotationPlan.Pick(choices, choices[0].Label));
        Assert.Same(choices[0], KeyRotationPlan.Pick(choices, "something else"));
    }

    [Fact]
    public void A_new_key_must_be_acceptable_and_different_from_the_current_one()
    {
        Assert.Null(KeyRotationPlan.Refusal("a-brand-new-lab-key", "the-current-lab-key"));
        Assert.Equal("That is already the current key.", KeyRotationPlan.Refusal("the-current-lab-key", "the-current-lab-key"));
        Assert.NotNull(KeyRotationPlan.Refusal("short", "the-current-lab-key"));
        Assert.NotEqual("That is already the current key.", KeyRotationPlan.Refusal("short", "short"));
        Assert.NotNull(KeyRotationPlan.Refusal("", "the-current-lab-key"));
    }

    [Theory]
    [InlineData("8", 8)]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    [InlineData("-1", 0)]
    [InlineData("x", 0)]
    public void The_batch_is_a_positive_number_and_anything_else_means_all_at_once(string typed, int expected) =>
        Assert.Equal(expected, KeyRotationPlan.Batch(typed));

    [Fact]
    public void The_computers_on_another_key_are_listed_with_their_key_ten_at_most()
    {
        var two = KeyRotationPlan.IncompatibleText([Pc("a", "Teachers"), Pc("b")]);
        Assert.StartsWith("Rotate shared key changes only the default lab key.", two);
        Assert.Contains("a — Teachers", two);
        Assert.Contains("b — key not identified", two);
        Assert.DoesNotContain("...", two);

        var ten = KeyRotationPlan.IncompatibleText(Enumerable.Range(1, 10).Select(i => Pc("pc" + i, "k")).ToList());
        Assert.Contains("pc10 — k", ten);
        Assert.DoesNotContain("...", ten);

        var twelve = KeyRotationPlan.IncompatibleText(Enumerable.Range(1, 12).Select(i => Pc("pc" + i, "k")).ToList());
        Assert.Contains("pc10 — k", twelve);
        Assert.DoesNotContain("pc11", twelve);
        Assert.EndsWith("\n...", twelve);
    }

    [Fact]
    public void The_summary_says_how_many_are_done_and_what_happens_to_the_rest()
    {
        Assert.Equal("5 of 5 computer(s) now use the new key.", KeyRotationPlan.Summary(5, 5, 0, autoFinish: true));
        Assert.Equal("3 of 5 computer(s) now use the new key. 2 computer(s) in the list are not confirmed on it yet; they are switched automatically the next time this app sees them.",
            KeyRotationPlan.Summary(3, 5, 2, autoFinish: true));
        Assert.Equal("3 of 5 computer(s) now use the new key. 2 computer(s) in the list are not confirmed on it yet; run Rotate key again for them.",
            KeyRotationPlan.Summary(3, 5, 2, autoFinish: false));
        Assert.DoesNotContain("not confirmed", KeyRotationPlan.Summary(1, 1, 0, autoFinish: false));
    }
}
