// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class AddressFollowerTests
{
    static ClientInfo Saved(string name, string address, string mac = "", string clientId = "", string room = "Lab 1", bool unreachable = true) =>
        new() { Name = name, Address = address, Mac = mac, ClientId = clientId, Room = room, Unreachable = unreachable };

    static Discovery.Found Answer(string address, string name, string mac = "", string clientId = "") =>
        new(IPAddress.Parse(address), new DiscoveryReply { MachineName = name, Mac = mac, ClientId = clientId });

    [Fact]
    public void A_computer_that_answers_from_another_address_is_recognised_by_its_network_card()
    {
        var pc18 = Saved("410-18", "10.42.4.51", mac: "C46516BDB1E2");
        var pc17 = Saved("410-17", "10.42.4.52", mac: "C46516BDAFE1");
        var found = new[]
        {
            Answer("10.42.4.96", "410-18", mac: "c4-65-16-bd-b1-e2"),
            Answer("10.42.4.50", "410-3", mac: "C46516BD3FFF"),
        };

        var moved = AddressFollower.Moved([pc18, pc17], found);

        var only = Assert.Single(moved);
        Assert.Same(pc18, only.Computer);
        Assert.Equal("10.42.4.96", only.Found.Address.ToString());
    }

    [Fact]
    public void A_name_alone_never_moves_an_entry_and_an_answer_from_the_saved_address_is_not_a_move()
    {
        // Classroom images often give many computers the same name.
        var sameName = Saved("LAB-PC", "192.168.1.20", mac: "AAAAAAAAAAAA");
        var stillThere = Saved("PC-02", "192.168.1.21", clientId: "id-02");
        var found = new[]
        {
            Answer("192.168.1.30", "LAB-PC", mac: "BBBBBBBBBBBB"),
            Answer("192.168.1.21", "PC-02", clientId: "id-02"),
        };

        Assert.Empty(AddressFollower.Moved([sameName, stillThere], found));
    }

    [Fact]
    public void A_client_id_is_enough_when_no_network_card_address_is_known_but_not_when_two_computers_share_it()
    {
        var pc = Saved("PC-03", "192.168.1.22", clientId: "id-03");

        var one = AddressFollower.Moved([pc], [Answer("192.168.1.40", "PC-03", clientId: "ID-03")]);
        var cloned = AddressFollower.Moved([pc], [Answer("192.168.1.40", "PC-03", clientId: "id-03"), Answer("192.168.1.41", "PC-04", clientId: "id-03")]);

        Assert.Single(one);
        Assert.Empty(cloned);
    }

    [Fact]
    public void Only_room_computers_that_do_not_answer_and_can_be_recognised_are_searched_for()
    {
        Assert.True(AddressFollower.IsMissing(Saved("a", "192.168.1.20", mac: "AAAAAAAAAAAA")));
        Assert.False(AddressFollower.IsMissing(Saved("a", "192.168.1.20", mac: "AAAAAAAAAAAA", unreachable: false)));
        Assert.False(AddressFollower.IsMissing(Saved("a", "192.168.1.20", mac: "AAAAAAAAAAAA", room: "")));   // discovered only, never polled
        Assert.False(AddressFollower.IsMissing(Saved("a", "192.168.1.20")));                                   // nothing to recognise it by
        var busy = Saved("a", "192.168.1.20", mac: "AAAAAAAAAAAA");
        busy.BusyCount = 1;
        Assert.False(AddressFollower.IsMissing(busy));
        var updating = Saved("a", "192.168.1.20", mac: "AAAAAAAAAAAA");
        updating.ExclusiveOperation = "A Classroom Client update is in progress.";
        Assert.False(AddressFollower.IsMissing(updating));
    }

    [Fact]
    public void An_address_that_now_answers_with_another_network_card_is_another_computer_unless_the_client_id_is_the_same()
    {
        var saved = Saved("410-17", "10.42.4.68", mac: "C46516BDAFE1", clientId: "id-17");

        Assert.True(AddressFollower.AnswersAsAnother(saved, "id-15", "C4-65-16-BD-B1-74"));
        Assert.True(AddressFollower.AnswersAsAnother(saved, "", "C46516BDB174"));
        Assert.False(AddressFollower.AnswersAsAnother(saved, "ID-17", "C46516BDB174"));   // a replaced network card
        Assert.False(AddressFollower.AnswersAsAnother(saved, "id-15", "c4-65-16-bd-af-e1"));   // the same card
        Assert.False(AddressFollower.AnswersAsAnother(saved, "id-15", ""));                    // no card to compare
        Assert.False(AddressFollower.AnswersAsAnother(Saved("x", "10.0.0.1"), "id-15", "C46516BDB174"));
    }
}
