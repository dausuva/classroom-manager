// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class OperationTargetSummaryTests
{
    [Fact]
    public void Empty_target_explains_that_selection_is_required()
    {
        var text = OperationTargetSummary.Format(Array.Empty<ClientInfo>());

        Assert.Equal("Target: no computers selected", text.Heading);
        Assert.Contains("Select computers", text.Detail);
    }

    [Fact]
    public void Selected_target_shows_room_and_availability()
    {
        var text = OperationTargetSummary.Format(
        [
            new ClientInfo { Room = "Lab A", Level = "ok" },
            new ClientInfo { Room = "Lab A", Level = "" },
        ]);

        Assert.Equal("Target: 2 selected computers", text.Heading);
        Assert.Equal("Room: Lab A  •  1 online  •  1 offline", text.Detail);
    }

    [Fact]
    public void Up_to_five_selected_computers_are_named_in_selection_order()
    {
        var text = OperationTargetSummary.Format(
        [
            new ClientInfo { Name = "PC-03" },
            new ClientInfo { Name = "PC-01" },
            new ClientInfo { Name = " ", Address = "10.0.0.7" },
        ]);

        Assert.Equal("Target: 3 selected computers — PC-03, PC-01, 10.0.0.7", text.Heading);
        Assert.Equal("Target: 1 selected computer — PC-01",
            OperationTargetSummary.Format([new ClientInfo { Name = "PC-01" }]).Heading);
    }

    [Fact]
    public void Larger_selections_name_the_first_five_and_count_the_rest()
    {
        var targets = Enumerable.Range(1, 12).Select(index => new ClientInfo { Name = $"PC-{index:00}" }).ToList();

        Assert.Equal("Target: 12 selected computers — PC-01, PC-02, PC-03, PC-04, PC-05 + 7 more",
            OperationTargetSummary.Format(targets).Heading);
        Assert.Equal("Target: 5 selected computers — PC-01, PC-02, PC-03, PC-04, PC-05",
            OperationTargetSummary.Format(targets.Take(5).ToList()).Heading);
    }

    [Fact]
    public void Mixed_room_target_reports_room_count_without_hiding_unassigned_computers()
    {
        var text = OperationTargetSummary.Format(
        [
            new ClientInfo { Room = "Lab A", Level = "ok" },
            new ClientInfo { Room = "Lab B", Level = "idle" },
            new ClientInfo { Room = "", Level = "" },
        ]);

        Assert.Equal("Target: 3 selected computers", text.Heading);
        Assert.Equal("Room: 3 rooms  •  2 online  •  1 offline", text.Detail);
    }
}
