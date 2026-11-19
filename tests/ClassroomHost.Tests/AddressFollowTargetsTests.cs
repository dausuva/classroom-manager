// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

// T-222: use the production follower's post-discovery admission/identity seam.
public sealed class AddressFollowTargetsTests
{
    static ClientInfo Computer() => new() { Address = "192.0.2.10", Room = "fixture", Unreachable = true, ClientId = "fixture-id", Mac = "AAAAAAAAAAAA" };
    static Discovery.Found Answer(string address = "192.0.2.20", string mac = "AAAAAAAAAAAA", string id = "fixture-id") =>
        new(IPAddress.Parse(address), new DiscoveryReply { MachineName = "same-name", ClientId = id, Mac = mac });

    [Theory]
    [InlineData("busy")]
    [InlineData("exclusive")]
    [InlineData("room")]
    [InlineData("reachable")]
    [InlineData("removed")]
    [InlineData("network")]
    [InlineData("address")]
    [InlineData("port")]
    [InlineData("id")]
    [InlineData("mac")]
    public void Changed_targets_are_not_moved_by_delayed_discovery(string change)
    {
        var client = Computer();
        var captured = StatusRefreshTarget.Capture(client);
        var roster = new List<ClientInfo> { client };
        var selected = true;
        switch (change)
        {
            case "busy": client.BusyCount = 1; break;
            case "exclusive": client.ExclusiveOperation = "fixture update"; break;
            case "room": client.Room = ""; break;
            case "reachable": client.Unreachable = false; break;
            case "removed": roster.Clear(); break;
            case "network": selected = false; break;
            case "address": client.Address = "192.0.2.30"; break;
            case "port": client.Port++; break;
            case "id": client.ClientId = "other"; break;
            case "mac": client.Mac = "BBBBBBBBBBBB"; break;
        }
        Assert.Empty(AddressFollowTargets.Plan([captured], roster, _ => selected, [Answer()]));
        Assert.False(AddressFollowTargets.IsMissing(captured, roster, _ => selected));
    }

    [Fact]
    public void Only_unique_stable_identity_moves_and_planning_never_mutates_roster_or_endpoint()
    {
        var client = Computer();
        var captured = StatusRefreshTarget.Capture(client);
        var roster = new[] { client };
        var answer = Answer();
        var move = Assert.Single(AddressFollowTargets.Plan([captured], roster, _ => true, [answer]));
        Assert.Same(captured, move.Target);
        Assert.Same(answer, move.Answer);
        Assert.Equal("192.0.2.10", client.Address);
        Assert.Single(roster);
        Assert.Empty(AddressFollowTargets.Plan([captured], roster, _ => true, [answer, Answer("192.0.2.21")]));
        Assert.Empty(AddressFollowTargets.Plan([captured], roster, _ => true, [Answer(mac: "BBBBBBBBBBBB")]));
        Assert.Empty(AddressFollowTargets.Plan([captured], roster, _ => true, [Answer("192.0.2.10")]));
    }

    [Fact]
    public void Duplicate_saved_identity_cannot_redirect_automatic_merge_into_a_busy_or_exclusive_row()
    {
        var client = Computer();
        var duplicate = Computer();
        duplicate.Address = "192.0.2.11";
        duplicate.BusyCount = 1;
        duplicate.ExclusiveOperation = "fixture update";
        var captured = StatusRefreshTarget.Capture(client);
        Assert.Empty(AddressFollowTargets.Plan([captured], new[] { client, duplicate }, _ => true, [Answer()]));
        Assert.Equal("192.0.2.11", duplicate.Address);
        Assert.Equal("fixture update", duplicate.ExclusiveOperation);
    }
}
