// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ThemeContrastTests
{
    static readonly string[] RequiredBrushes =
    [
        "WindowBackgroundBrush", "WindowForegroundBrush", "ControlBackgroundBrush", "ControlForegroundBrush",
        "MutedTextBrush", "SelectionBackgroundBrush", "SelectionTextBrush", "TabStripBackgroundBrush",
        "TabBackgroundBrush", "TabHoverBrush", "TabSelectedBackgroundBrush", "TabSelectedForegroundBrush",
        "TabBorderBrush", "TitleBarBackgroundBrush", "TitleBarForegroundBrush", "TitleBarBorderBrush",
        "AccentBrush", "AccentTextBrush", "DangerBrush", "InputBorderBrush", "ButtonBorderBrush",
        "StateOkBrush", "StateIdleBrush", "StateWarnBrush", "StateBadBrush", "StateUnknownBrush",
    ];

    // ---- every pair of colours the Host actually puts on top of each other ---------------------------

    /// <summary>Text and the surfaces it is drawn on; each must reach 4.5:1.</summary>
    public static TheoryData<string, string, string> TextPairs()
    {
        var pairs = new TheoryData<string, string, string>();
        foreach (var theme in new[] { "Light.xaml", "Dark.xaml" })
        {
            foreach (var surface in new[] { "WindowBackgroundBrush", "HeaderBackgroundBrush", "ButtonBackgroundBrush", "TabBackgroundBrush", "TabHoverBrush", "TabStripBackgroundBrush" })
                pairs.Add(theme, "WindowForegroundBrush", surface);                          // labels, button text and icons, tabs
            foreach (var surface in new[] { "ControlBackgroundBrush", "GridAlternateRowBrush", "ItemHoverBrush" })
                pairs.Add(theme, "ControlForegroundBrush", surface);                         // boxes, lists and table rows
            foreach (var surface in new[] { "ButtonHoverBrush", "ButtonPressedBrush" })
                pairs.Add(theme, "ButtonInteractiveTextBrush", surface);                     // a button under the pointer
            foreach (var surface in new[] { "WindowBackgroundBrush", "ControlBackgroundBrush", "HeaderBackgroundBrush", "GridAlternateRowBrush" })
                pairs.Add(theme, "MutedTextBrush", surface);                                 // hints and status lines
            foreach (var surface in new[] { "ButtonBackgroundBrush", "ButtonHoverBrush" })
                pairs.Add(theme, "DangerBrush", surface);                                    // destructive buttons
            pairs.Add(theme, "AccentTextBrush", "AccentBrush");                              // primary buttons
            pairs.Add(theme, "SelectionTextBrush", "SelectionBackgroundBrush");
            pairs.Add(theme, "TabSelectedForegroundBrush", "TabSelectedBackgroundBrush");
        }
        return pairs;
    }

    /// <summary>Outlines and state dots, which have to be told apart from what is behind them: 3:1.</summary>
    public static TheoryData<string, string, string> ShapePairs()
    {
        var pairs = new TheoryData<string, string, string>();
        foreach (var theme in new[] { "Light.xaml", "Dark.xaml" })
        {
            foreach (var outline in new[] { "ButtonBorderBrush", "InputBorderBrush", "AccentBrush" })   // AccentBrush is the focus outline
                foreach (var surface in new[] { "WindowBackgroundBrush", "HeaderBackgroundBrush", "ControlBackgroundBrush" })
                    pairs.Add(theme, outline, surface);
            foreach (var dot in new[] { "StateOkBrush", "StateIdleBrush", "StateWarnBrush", "StateBadBrush", "StateUnknownBrush" })
                foreach (var row in new[] { "ControlBackgroundBrush", "GridAlternateRowBrush", "ItemHoverBrush", "SelectionBackgroundBrush" })
                    pairs.Add(theme, dot, row);
        }
        return pairs;
    }

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void Text_can_be_read_on_every_surface_it_appears_on(string theme, string text, string surface)
    {
        var colors = ThemeColors(theme);
        var ratio = Contrast(colors[text], colors[surface]);
        Assert.True(ratio >= 4.5, $"{theme}: {text} {colors[text]} on {surface} {colors[surface]} is {ratio:0.00}:1, below 4.5:1.");
    }

    [Theory]
    [MemberData(nameof(ShapePairs))]
    public void Outlines_and_state_dots_stand_out_from_what_is_behind_them(string theme, string shape, string surface)
    {
        var colors = ThemeColors(theme);
        var ratio = Contrast(colors[shape], colors[surface]);
        Assert.True(ratio >= 3.0, $"{theme}: {shape} {colors[shape]} on {surface} {colors[surface]} is {ratio:0.00}:1, below 3:1.");
    }

    [Fact]
    public void No_colour_is_written_into_the_Host_outside_the_theme_files()
    {
        // A colour typed into a window is the same in the light and the dark theme, so it is wrong in one of them.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "ClassroomHost", "Themes")))
            directory = directory.Parent;
        var host = Path.Combine(directory!.FullName, "src", "ClassroomHost");
        var found = Directory.EnumerateFiles(host, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(@"\obj\") && !path.Contains(@"\bin\") && !path.Contains(@"\Themes\"))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index))
                .Where(entry => System.Text.RegularExpressions.Regex.IsMatch(entry.line, @"=""#[0-9A-Fa-f]{6,8}""")))
            .Select(entry => $"{Path.GetFileName(entry.path)}:{entry.index + 1} {entry.line.Trim()}").ToList();

        Assert.True(found.Count == 0, "Use a theme brush (DynamicResource) instead:\n" + string.Join("\n", found));
    }

    [Theory]
    [InlineData("Light.xaml")]
    [InlineData("Dark.xaml")]
    [InlineData("HighContrast.xaml")]
    public void Theme_defines_the_complete_surface_color_contract(string file)
    {
        var colors = ThemeColors(file);
        Assert.All(RequiredBrushes, key => Assert.True(colors.ContainsKey(key), $"{file} is missing {key}."));
    }

    [Theory]
    [InlineData("Light.xaml")]
    [InlineData("Dark.xaml")]
    public void Standard_theme_text_pairs_meet_normal_text_contrast(string file)
    {
        var colors = ThemeColors(file);
        AssertContrast(colors, "WindowForegroundBrush", "WindowBackgroundBrush");
        AssertContrast(colors, "MutedTextBrush", "WindowBackgroundBrush");
        AssertContrast(colors, "ControlForegroundBrush", "ControlBackgroundBrush");
        AssertContrast(colors, "SelectionTextBrush", "SelectionBackgroundBrush");
        AssertContrast(colors, "TabSelectedForegroundBrush", "TabSelectedBackgroundBrush");
        AssertContrast(colors, "TitleBarForegroundBrush", "TitleBarBackgroundBrush");
    }

    static void AssertContrast(IReadOnlyDictionary<string, string> colors, string foreground, string background)
    {
        var ratio = Contrast(colors[foreground], colors[background]);
        Assert.True(ratio >= 4.5, $"{foreground} on {background} has only {ratio:0.00}:1 contrast.");
    }

    static Dictionary<string, string> ThemeColors(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "ClassroomHost", "Themes")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var document = XDocument.Load(Path.Combine(directory!.FullName, "src", "ClassroomHost", "Themes", file));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return document.Descendants()
            .Where(element => element.Name.LocalName == "SolidColorBrush" && element.Attribute(x + "Key") is not null)
            .ToDictionary(element => element.Attribute(x + "Key")!.Value,
                element => element.Attribute("Color")?.Value ?? "", StringComparer.Ordinal);
    }

    static double Contrast(string first, string second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    static double Luminance(string hex)
    {
        Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
        var channels = new[] { hex[1..3], hex[3..5], hex[5..7] }
            .Select(value => int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d)
            .Select(value => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4))
            .ToArray();
        return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
    }
}
