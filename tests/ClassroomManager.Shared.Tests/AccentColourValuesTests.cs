// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>
/// The registry values written for a Windows accent colour (docs/QUALITY.md: the mutation test found the arithmetic unwatched). The expected numbers were worked out
/// independently of the code (the same formulas in another language) for two colours: Windows' own default blue and a red.
/// </summary>
public sealed class AccentColourValuesTests
{
    static AccentColours.RegistryValue Value(IReadOnlyList<AccentColours.RegistryValue> values, string key, string name) =>
        values.Single(value => value.Key == key && value.Name == name);

    [Fact]
    public void Default_blue_is_written_as_the_numbers_Windows_keeps()
    {
        var values = AccentColours.ValuesFor("0078D7", null);
        Assert.Equal(-2656256, Value(values, AccentColours.ExplorerAccentKey, "AccentColorMenu").Value);     // 0xFFD77800: alpha, blue, green, red
        Assert.Equal(-5480448, Value(values, AccentColours.ExplorerAccentKey, "StartColorMenu").Value);      // the colour 20 % darker
        Assert.Equal(-2656256, Value(values, AccentColours.DwmKey, "AccentColor").Value);
        Assert.Equal(-1006602025, Value(values, AccentColours.DwmKey, "ColorizationColor").Value);           // 0xC40078D7: alpha C4, then red, green, blue
        Assert.Equal(0, Value(values, AccentColours.DesktopKey, "AutoColorization").Value);                   // "pick from my picture" off
        Assert.Equal(new byte[]
        {
            153, 201, 239, 0, 102, 174, 231, 0, 51, 147, 223, 0, 0, 120, 215, 0,   // three lighter, the colour
            0, 96, 172, 0, 0, 72, 129, 0, 0, 48, 86, 0, 0, 0, 0, 0,                // three darker, the spare
        }, (byte[])Value(values, AccentColours.ExplorerAccentKey, "AccentPalette").Value);
        Assert.DoesNotContain(values, value => value.Name == "ColorPrevalence");
    }

    [Fact]
    public void A_red_gives_the_numbers_for_a_colour_with_every_channel_set()
    {
        var values = AccentColours.ValuesFor("FF4343", true);
        Assert.Equal(-12368897, Value(values, AccentColours.ExplorerAccentKey, "AccentColorMenu").Value);
        Assert.Equal(-13224244, Value(values, AccentColours.ExplorerAccentKey, "StartColorMenu").Value);
        Assert.Equal(-989904061, Value(values, AccentColours.DwmKey, "ColorizationColor").Value);
        Assert.Equal(new byte[]
        {
            255, 180, 180, 0, 255, 142, 142, 0, 255, 105, 105, 0, 255, 67, 67, 0,
            204, 54, 54, 0, 153, 40, 40, 0, 102, 27, 27, 0, 0, 0, 0, 0,
        }, (byte[])Value(values, AccentColours.ExplorerAccentKey, "AccentPalette").Value);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Colour_on_title_bars_is_written_only_when_asked_for(bool bars, int expected) =>
        Assert.Equal(expected, Value(AccentColours.ValuesFor("0078D7", bars), AccentColours.DwmKey, "ColorPrevalence").Value);

    [Fact]
    public void The_Windows_default_is_the_default_blue_with_no_colour_on_title_bars_unless_asked()
    {
        var values = AccentColours.ValuesForDefault(null);
        Assert.Equal(0, Value(values, AccentColours.DwmKey, "ColorPrevalence").Value);
        Assert.Equal(AccentColours.ValuesFor(AccentColours.DefaultRgb, false).Select(value => value.Name), values.Select(value => value.Name));
        Assert.Equal(1, Value(AccentColours.ValuesForDefault(true), AccentColours.DwmKey, "ColorPrevalence").Value);
        Assert.Equal("0078D4", AccentColours.DefaultRgb);
    }

    [Theory]
    [InlineData("0078D7", unchecked((int)0xFFD77800))]
    [InlineData("FF4343", unchecked((int)0xFF4343FF))]
    public void A_colour_read_back_from_the_Accent_key_is_six_digits_in_red_green_blue_order(string rgb, int abgr) =>
        Assert.Equal(rgb, AccentColours.RgbFromAbgr(abgr));

    [Fact]
    public void A_colour_is_known_by_the_name_Windows_gives_it_in_any_case_or_not_at_all()
    {
        Assert.Equal("Default blue", AccentColours.NameOf("0078D7"));
        Assert.Equal("Default blue", AccentColours.NameOf("0078d7"));
        Assert.Equal("Red", AccentColours.NameOf("E81123"));
        Assert.Equal("", AccentColours.NameOf("123456"));
        Assert.Equal("", AccentColours.NameOf(""));
    }

    [Fact]
    public void The_palette_has_the_forty_eight_colours_of_the_picker_each_once_in_six_upper_case_digits()
    {
        Assert.Equal(48, AccentColours.Palette.Length);
        Assert.Equal(48, AccentColours.Palette.Select(colour => colour.Rgb).Distinct().Count());
        Assert.Equal(48, AccentColours.Palette.Select(colour => colour.Name).Distinct().Count());
        Assert.All(AccentColours.Palette, colour => Assert.Matches("^[0-9A-F]{6}$", colour.Rgb));
        Assert.Equal("Yellow gold", AccentColours.Palette[0].Name);
        Assert.Equal("FFB900", AccentColours.Palette[0].Rgb);
        Assert.Equal("Camouflage", AccentColours.Palette[^1].Name);
        Assert.Equal("7E735F", AccentColours.Palette[^1].Rgb);
    }

    [Theory]
    [InlineData("#0078d7", "0078D7")]
    [InlineData(" 0078D7 ", "0078D7")]
    [InlineData("##0078D7", "0078D7")]
    [InlineData("0078D4", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("nonsense", null)]
    public void Only_a_colour_of_the_palette_is_accepted_typed_with_or_without_the_hash(string? typed, string? expected) =>
        Assert.Equal(expected, AccentColours.InPalette(typed));

    [Fact]
    public void The_request_carries_the_accent_and_the_title_bar_choice_and_nothing_for_unchanged()
    {
        var args = new JsonObject();
        AccentColours.AddTo(args, "0078D7", true);
        Assert.Equal("0078D7", (string?)args[AccentColours.Accent]);
        Assert.True((bool?)args[AccentColours.TitleBars]);
        var nothing = new JsonObject();
        AccentColours.AddTo(nothing, AccentColours.Unchanged, null);
        Assert.Empty(nothing);
        var onlyBars = new JsonObject();
        AccentColours.AddTo(onlyBars, AccentColours.Unchanged, false);
        Assert.Null(onlyBars[AccentColours.Accent]);
        Assert.False((bool?)onlyBars[AccentColours.TitleBars]);
        Assert.True(AccentColours.Supported("1.16"));
        Assert.False(AccentColours.Supported("1.15"));
        Assert.False(AccentColours.Supported(null));
    }
}
