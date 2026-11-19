// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>The words shown before a lab key change: who gets the key now, who waits, and what must be typed to go on.</summary>
public sealed class KeyChangePlanTests
{
    static ClientInfo Computer(string name, string status) => new() { Name = name, Status = status };

    [Fact]
    public void Computers_that_answer_get_the_key_now_and_the_others_wait_with_their_names_and_the_grace_window()
    {
        var plan = KeyChangePlan.Build([Computer("410-01", "Online"), Computer("410-02", "Offline (no answer)"), Computer("410-03", "Online - nobody signed in")], 14, "Lab key");

        Assert.Equal(["410-01", "410-03"], plan.Online);
        Assert.Equal(["410-02"], plan.Offline);
        var text = plan.Describe();
        Assert.Contains("2 computer(s) answer now", text);
        Assert.Contains("410-02", text);
        Assert.Contains("14 days", text);
        Assert.Contains("backup", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void When_every_computer_answers_nothing_is_said_about_waiting()
    {
        var plan = KeyChangePlan.Build([Computer("410-01", "Online")], 14, "Lab key");

        Assert.Empty(plan.Offline);
        Assert.DoesNotContain("14 days", plan.Describe());
    }

    [Theory]
    [InlineData("Lab key", true)]
    [InlineData("  lab KEY ", true)]
    [InlineData("", false)]
    [InlineData("yes", false)]
    public void Only_the_name_of_the_lab_key_confirms_the_change(string typed, bool confirms) =>
        Assert.Equal(confirms, KeyChangePlan.Build([], 14, "Lab key").Confirms(typed));

    [Fact]
    public void A_long_list_of_waiting_computers_is_cut_to_eight_names()
    {
        var plan = KeyChangePlan.Build(Enumerable.Range(1, 12).Select(i => Computer($"PC-{i:00}", "Offline")), 14, "Lab key");

        var text = plan.Describe();
        Assert.Contains("PC-08", text);
        Assert.DoesNotContain("PC-09", text);
        Assert.Contains("12 computer(s) are off", text);
    }
}
