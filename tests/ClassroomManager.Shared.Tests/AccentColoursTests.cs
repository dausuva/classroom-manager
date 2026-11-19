// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>T-168: the accent colour a Client is asked to set, and the registry values that become of it.</summary>
public sealed class AccentColoursTests
{
    [Fact]
    public void The_palette_has_the_forty_eight_colours_of_windows_once_each()
    {
        Assert.Equal(48, AccentColours.Palette.Length);
        Assert.Equal(48, AccentColours.Palette.Select(c => c.Rgb).Distinct().Count());
        Assert.Equal(48, AccentColours.Palette.Select(c => c.Name).Distinct().Count());
        Assert.All(AccentColours.Palette, c => Assert.Matches("^[0-9A-F]{6}$", c.Rgb));
    }

    [Theory]
    [InlineData("#0078D7", "0078D7")]
    [InlineData("0078d7", "0078D7")]
    [InlineData("  #ffb900 ", "FFB900")]
    public void A_colour_of_the_palette_is_accepted_in_any_writing(string text, string expected)
    {
        Assert.Equal(expected, AccentColours.InPalette(text));
    }

    [Theory]
    [InlineData("#123456")]
    [InlineData("red")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? text)
    {
        Assert.Null(AccentColours.InPalette(text));
    }

    [Fact]
    public void A_colour_is_written_as_windows_keeps_it()
    {
        var values = AccentColours.ValuesFor("0078D7", null).ToDictionary(v => (v.Key, v.Name), v => v.Value);

        // Windows keeps 0xAABBGGRR: blue and red swap places.
        Assert.Equal(unchecked((int)0xFFD77800), values[(AccentColours.ExplorerAccentKey, "AccentColorMenu")]);
        Assert.Equal(unchecked((int)0xFFD77800), values[(AccentColours.DwmKey, "AccentColor")]);
        Assert.Equal(unchecked((int)0xC40078D7), values[(AccentColours.DwmKey, "ColorizationColor")]);
        Assert.Equal(0, values[(AccentColours.DesktopKey, "AutoColorization")]);
    }

    [Fact]
    public void The_palette_of_shades_is_thirty_two_bytes_and_holds_the_colour_in_the_middle()
    {
        var palette = (byte[])AccentColours.ValuesFor("0078D7", null).Single(v => v.Name == "AccentPalette").Value;

        Assert.Equal(32, palette.Length);
        Assert.Equal([0x00, 0x78, 0xD7, 0x00], palette[12..16]);
        Assert.True(palette[2] > palette[10] && palette[10] > palette[14], "lighter shades come first");
        Assert.True(palette[22] < palette[14], "darker shades follow");
    }

    [Fact]
    public void Title_bars_are_only_touched_when_asked()
    {
        Assert.DoesNotContain(AccentColours.ValuesFor("0078D7", null), v => v.Name == "ColorPrevalence");
        Assert.Equal(1, AccentColours.ValuesFor("0078D7", true).Single(v => v.Name == "ColorPrevalence").Value);
        Assert.Equal(0, AccentColours.ValuesFor("0078D7", false).Single(v => v.Name == "ColorPrevalence").Value);
    }

    [Fact]
    public void The_default_puts_the_windows_blue_back_and_takes_the_colour_off_title_bars()
    {
        var values = AccentColours.ValuesForDefault(null).ToDictionary(v => (v.Key, v.Name), v => v.Value);

        Assert.Equal(unchecked((int)0xFFD47800), values[(AccentColours.ExplorerAccentKey, "AccentColorMenu")]);
        Assert.Equal(0, values[(AccentColours.DwmKey, "ColorPrevalence")]);
    }

    [Theory]
    [InlineData(unchecked((int)0xFFD77800), "0078D7")]
    [InlineData(unchecked((int)0xFF0000FF), "FF0000")]
    public void The_colour_is_read_back_from_the_stored_number(int stored, string expected)
    {
        Assert.Equal(expected, AccentColours.RgbFromAbgr(stored));
    }

    [Fact]
    public void Nothing_is_added_to_a_request_that_changes_nothing()
    {
        var args = new JsonObject();

        AccentColours.AddTo(args, AccentColours.Unchanged, null);

        Assert.Empty(args);
    }

    [Fact]
    public void A_request_carries_what_was_chosen()
    {
        var args = new JsonObject();

        AccentColours.AddTo(args, "#0078D7", false);

        Assert.Equal("#0078D7", args[AccentColours.Accent]!.GetValue<string>());
        Assert.False(args[AccentColours.TitleBars]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("1.16", true)]
    [InlineData("1.15", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_client_with_the_option_is_asked_for_a_colour(string? version, bool expected)
    {
        Assert.Equal(expected, AccentColours.Supported(version));
    }

    [Fact]
    public void The_protocol_version_names_the_option()
    {
        Assert.True(AccentColours.Supported(Protocol.Version));
    }
}
