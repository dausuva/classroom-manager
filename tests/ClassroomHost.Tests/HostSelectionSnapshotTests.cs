// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class HostSelectionSnapshotTests
{
    static ClientInfo Client(string name, bool selected = false) => new() { Name = name, IsSelected = selected };

    // The same fixtures first passed against the former SelectedRaw/UpdateCollectionState expressions.
    static Projection Capture(IEnumerable<ClientInfo> visible, int savedCount, bool networkMatch,
        IReadOnlyList<ClientInfo>? targetOverride = null)
    {
        var snapshot = HostSelectionSnapshot.Capture(visible, savedCount, networkMatch, targetOverride);
        var reason = snapshot.EmptyReason switch
        {
            HostEmptyStateReason.NoSavedComputers => "computers",
            HostEmptyStateReason.NoNetworkMatch => "network",
            HostEmptyStateReason.NoRoomMatch => "room",
            _ => "none",
        };
        return new(snapshot.Targets, snapshot.VisibleCount, reason);
    }

    sealed record Projection(IReadOnlyList<ClientInfo> Targets, int VisibleCount, string EmptyReason);

    [Fact]
    public void Only_checked_visible_computers_are_targets_in_visible_order()
    {
        var hidden = Client("hidden", true);
        var first = Client("first", true);
        var last = Client("last", true);
        var snapshot = Capture([last, Client("unchecked"), first], 4, true);
        Assert.Equal(new[] { last, first }, snapshot.Targets);
        Assert.DoesNotContain(hidden, snapshot.Targets);
        Assert.Equal(3, snapshot.VisibleCount);
        Assert.Equal("none", snapshot.EmptyReason);
    }

    [Fact]
    public void Override_wins_even_for_a_hidden_unchecked_computer()
    {
        var target = Client("single");
        Assert.Equal(new[] { target }, Capture([Client("checked", true)], 2, true, [target]).Targets);
        Assert.False(target.IsSelected);
    }

    [Fact]
    public void Empty_override_does_not_fall_back_to_checked_computers()
    {
        Assert.Empty(Capture([Client("checked", true)], 1, true, []).Targets);
    }

    [Fact]
    public void Membership_is_copied_not_a_live_enumeration()
    {
        var client = Client("first", true);
        var shown = new List<ClientInfo> { client };
        var snapshot = Capture(shown, 1, true);
        shown.Clear();
        client.IsSelected = false;
        Assert.Same(client, Assert.Single(snapshot.Targets));
        Assert.Equal(1, snapshot.VisibleCount);
    }

    [Fact]
    public void Override_order_and_membership_are_copied()
    {
        var a = Client("a");
        var b = Client("b");
        var targets = new List<ClientInfo> { b, a };
        var snapshot = Capture([], 2, true, targets);
        targets.Clear();
        Assert.Equal(new[] { b, a }, snapshot.Targets);
        Assert.Equal(0, snapshot.VisibleCount);
    }

    [Theory]
    [InlineData(0, false, "computers")]
    [InlineData(0, true, "computers")]
    [InlineData(3, false, "network")]
    [InlineData(3, true, "room")]
    public void Empty_roster_reasons_preserve_existing_precedence(int saved, bool networkMatch, string expected)
    {
        Assert.Equal(expected, Capture([], saved, networkMatch).EmptyReason);
    }

    [Fact]
    public void Visible_unchecked_computers_are_not_an_empty_roster()
    {
        var snapshot = Capture([Client("visible")], 1, true);
        Assert.Empty(snapshot.Targets);
        Assert.Equal(1, snapshot.VisibleCount);
        Assert.Equal("none", snapshot.EmptyReason);
    }

    [Fact]
    public void Selection_buttons_retain_hidden_selection_rules()
    {
        var visible = Client("visible", true);
        var hidden = Client("hidden", true);
        ComputerSelection.Invert([visible]);
        Assert.False(visible.IsSelected);
        Assert.True(hidden.IsSelected);
        ComputerSelection.Only([visible], [visible, hidden], _ => true);
        Assert.True(visible.IsSelected);
        Assert.False(hidden.IsSelected);
    }

    [Fact]
    public void Snapshot_membership_is_read_only_but_retains_live_client_identity()
    {
        var client = Client("a", true);
        var snapshot = HostSelectionSnapshot.Capture([client], 1, true);
        var collection = Assert.IsAssignableFrom<IList<ClientInfo>>(snapshot.Targets);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Clear());
        client.Status = "Online";
        Assert.Equal("Online", Assert.Single(snapshot.Targets).Status);
        Assert.False(snapshot.IsEmpty);
    }

    [Fact]
    public void Target_override_does_not_enumerate_the_visible_roster()
    {
        static IEnumerable<ClientInfo> Unavailable()
        {
            yield return Fail();
            static ClientInfo Fail() => throw new InvalidOperationException("This view must not be enumerated for an override.");
        }
        var client = Client("single");
        var targets = HostSelectionSnapshot.SelectTargets(Unavailable(), [client]);
        Assert.Same(client, Assert.Single(targets));
        Assert.Empty(HostSelectionSnapshot.SelectTargets(Unavailable(), []));
    }

    [Fact]
    public void Direct_target_capture_is_a_copy_and_matches_full_projection()
    {
        var shown = new List<ClientInfo> { Client("first", true), Client("unchecked"), Client("last", true) };
        var targets = HostSelectionSnapshot.SelectTargets(shown);
        Assert.Equal(Capture(shown, 3, true).Targets, targets);
        shown.Clear();
        Assert.Equal(2, targets.Count);
    }

    [Fact]
    public void Full_projection_enumerates_the_visible_input_once()
    {
        var enumerations = 0;
        IEnumerable<ClientInfo> Visible()
        {
            enumerations++;
            if (enumerations > 1) throw new InvalidOperationException("The view was read twice.");
            yield return Client("first", true);
            yield return Client("unchecked");
        }
        var snapshot = HostSelectionSnapshot.Capture(Visible(), 2, true);
        Assert.Equal(1, enumerations);
        Assert.Equal(2, snapshot.VisibleCount);
        Assert.Single(snapshot.Targets);
    }
}
