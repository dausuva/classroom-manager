// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class KeyboardLayoutComparisonTests
{
    const string Us = "0409:00000409";
    const string LtStandard = "0427:00010427";
    const string Russian = "0419:00000419";

    static ComputerKeyboard Keyboard(string chosen, params string[] tips) =>
        new(tips.Select(tip => new ComputerKeyboardLayout(tip, KeyboardLayouts.LanguageOf(tip), tip == Us ? "US" : "")).ToList(), chosen);

    [Fact]
    public void Layouts_are_compared_across_computers_with_the_room_default()
    {
        var pc1 = new ClientInfo { Name = "PC-01" };
        var pc2 = new ClientInfo { Name = "PC-02" };
        var rows = KeyboardLayoutComparison.Rows(new Dictionary<ClientInfo, ComputerKeyboard>
        {
            [pc1] = Keyboard("", Us, LtStandard),
            [pc2] = Keyboard(LtStandard, LtStandard, Russian),
        }, new KeyboardRoomDefault { Layouts = [Us, LtStandard], Default = LtStandard });

        var lithuanian = rows.Single(row => row.Tip == LtStandard);
        Assert.Equal("2 of 2", lithuanian.Installed);
        Assert.Equal("PC-02", lithuanian.DefaultOn);
        Assert.Equal("Yes, default", lithuanian.InRoomDefault);
        Assert.Equal("Lithuanian - Lithuanian Standard", lithuanian.Name);
        Assert.Equal("lt-LT", lithuanian.Language);

        var us = rows.Single(row => row.Tip == Us);
        Assert.Equal("1 of 2", us.Installed);
        Assert.Equal("PC-02", us.MissingFrom);
        Assert.Equal("PC-01", us.DefaultOn);  // nothing chosen: the first layout is the one a student gets
        Assert.Equal("US", us.Name);
        Assert.Equal("", rows.Single(row => row.Tip == Russian).InRoomDefault);
    }

    [Fact]
    public void Matching_the_room_default_adds_what_is_missing_and_removes_others_only_when_asked()
    {
        var room = new KeyboardRoomDefault { Layouts = [Us, LtStandard], Default = LtStandard };
        var computer = Keyboard("", Us, Russian);

        var (add, keep, makeDefault) = KeyboardLayoutComparison.Plan(computer, room, removeOthers: false);
        Assert.Equal([LtStandard], add);
        Assert.Empty(keep);
        Assert.Equal(LtStandard, makeDefault);
        var (_, remove, _) = KeyboardLayoutComparison.Plan(computer, room, removeOthers: true);
        Assert.Equal([Russian], remove);
        var (nothingToAdd, nothingToRemove, defaultAlready) = KeyboardLayoutComparison.Plan(Keyboard(LtStandard, LtStandard, Us), room, removeOthers: true);
        Assert.Empty(nothingToAdd);
        Assert.Empty(nothingToRemove);
        Assert.Equal("", defaultAlready);
    }

    [Fact]
    public void A_clients_answer_is_read_with_upper_case_names_and_bad_entries_left_out()
    {
        var keyboard = ComputerKeyboard.FromJson(new JsonObject
        {
            [KeyboardLayouts.Layouts] = new JsonArray(
                new JsonObject { ["tip"] = "0427:00010427", ["language"] = "lt-LT", ["name"] = "Lithuanian Standard" },
                new JsonObject { ["tip"] = "nonsense", ["language"] = "", ["name"] = "" },
                new JsonObject { ["tip"] = "0411:{03b5835f-f03c-411b-9ce2-aa23e1171e36}{a76c93d9-5523-4e90-aafa-4db112f9ac76}", ["language"] = "ja-JP", ["name"] = "" }),
            [KeyboardLayouts.Default] = "",
        });
        Assert.Equal(2, keyboard.Layouts.Count);
        Assert.Equal("0411:{03B5835F-F03C-411B-9CE2-AA23E1171E36}{A76C93D9-5523-4E90-AAFA-4DB112F9AC76}", keyboard.Layouts[1].Tip);
        Assert.Equal("0427:00010427", keyboard.Effective);
    }
}
