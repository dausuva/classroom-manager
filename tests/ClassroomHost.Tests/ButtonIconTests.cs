// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml.Linq;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// Catches the three ways a Host button ends up without a visible icon: a Tag symbol the icon
/// converter does not know (drawn as an empty box), a glyph that one of the two Windows icon fonts
/// lacks (blank on that Windows version), and a button with neither a Tag nor a label an icon can
/// be chosen from. The first and third are found by reading the Host's own XAML and C# sources.
/// </summary>
public sealed class ButtonIconTests
{
    static readonly string HostSource = Path.Combine(RepositoryRoot(), "src", "ClassroomHost");

    static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "ClassroomManager.sln"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("ClassroomManager.sln was not found above the test folder.");
    }

    static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(HostSource, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains(@"\obj\") && !path.Contains(@"\bin\"));

    static IReadOnlyDictionary<string, string> Glyphs() =>
        typeof(FluentIcons).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!);

    static string Where(string path, string text, int index) =>
        $"{Path.GetRelativePath(HostSource, path)}:{text.Take(index).Count(character => character == '\n') + 1}";

    /// <summary>Every button written in XAML: where it is, its Tag and its label (null when absent or bound).</summary>
    static IEnumerable<(string Where, string? Tag, string? Label)> XamlButtons()
    {
        foreach (var path in SourceFiles("*.xaml"))
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants().Where(element => element.Name.LocalName is "Button" or "ToggleButton"))
            {
                string? Literal(string name) => element.Attribute(name)?.Value is { } value && !value.StartsWith('{') ? value : null;
                var line = ((System.Xml.IXmlLineInfo)element).LineNumber;
                yield return ($"{Path.GetRelativePath(HostSource, path)}:{line}", Literal("Tag"), Literal("Content"));
            }
        }
    }

    /// <summary>Every button created in C# with an object initializer, with the Tag and Content written there.</summary>
    static IEnumerable<(string Where, string? Tag, string? Label)> CodeButtons()
    {
        foreach (var path in SourceFiles("*.cs"))
        {
            var text = File.ReadAllText(path);
            foreach (Match button in Regex.Matches(text, @"new (?:Toggle)?Button\s*\{([^{}]*)\}"))
            {
                var body = button.Groups[1].Value;
                var tag = Regex.Match(body, @"\bTag\s*=\s*(""(?<literal>[^""]*)""|(?<expression>[^,}]+))");
                // Only a label written out in full; one put together at run time ("_" + name) cannot be judged here.
                var label = Regex.Match(body, @"\bContent\s*=\s*""(?<literal>[^""]*)""(?=\s*(,|\z))");
                yield return (Where(path, text, button.Index),
                    tag.Success ? tag.Groups["literal"].Success ? Regex.Unescape(tag.Groups["literal"].Value) : "(expression)" : null,
                    label.Success ? label.Groups["literal"].Value : null);
            }

            // Many windows build their buttons through a small local helper: Button("label", "icon", ...)
            // where the second text is an icon, or Button("label", "tooltip", ...) where it is not.
            foreach (Match helper in Regex.Matches(text, @"\bButton (?<name>\w+)\(string \w+, (?:string (?<second>\w+))?"))
            {
                bool takesIcon = helper.Groups["second"].Value is "icon" or "tag" or "glyph";
                var calls = Regex.Matches(text, $@"(?<![\w.]){helper.Groups["name"].Value}\(\s*""(?<label>[^""]*)""(?:,\s*""(?<second>[^""]*)"")?");
                foreach (Match call in calls)
                    yield return (Where(path, text, call.Index),
                        takesIcon && call.Groups["second"].Success ? Regex.Unescape(call.Groups["second"].Value) : null,
                        call.Groups["label"].Value);
            }
        }
    }

    [Fact]
    public void Every_icon_exists_in_both_Windows_icon_fonts()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        // Windows 10 has only Segoe MDL2 Assets; Windows 11 has both and prefers Segoe Fluent Icons.
        var checkedFonts = 0;
        foreach (var file in new[] { "segmdl2.ttf", "SegoeIcons.ttf" })
        {
            var path = Path.Combine(fonts, file);
            if (!File.Exists(path)) continue;
            checkedFonts++;
            var font = new GlyphTypeface(new Uri(path));
            var missing = Glyphs().Where(glyph => glyph.Value.Length != 1 || !font.CharacterToGlyphMap.ContainsKey(glyph.Value[0]))
                .Select(glyph => $"{glyph.Key} (U+{(int)glyph.Value[0]:X4})").ToList();
            Assert.True(missing.Count == 0, $"{file} has no glyph for: {string.Join(", ", missing)}. Those buttons would show an empty box.");
        }
        Assert.True(checkedFonts > 0, "Neither Windows icon font was found on this computer.");
    }

    [Fact]
    public void Every_button_Tag_is_a_symbol_the_icon_converter_knows()
    {
        var glyphs = Glyphs().Values.ToHashSet();
        var unknown = XamlButtons().Concat(CodeButtons())
            .Where(button => button.Tag is { Length: > 0 } tag && tag != "(expression)" && !glyphs.Contains(FluentIconConverter.Resolve(tag)))
            .Select(button => $"{button.Where} '{button.Label}' Tag {string.Join(" ", button.Tag!.EnumerateRunes().Select(rune => $"U+{rune.Value:X4}"))}")
            .ToList();

        Assert.True(unknown.Count == 0,
            "Add these Tag symbols to FluentIconConverter's map (or use a FluentIcons constant); as they are, the button shows an empty box:\n" +
            string.Join("\n", unknown));
    }

    [Fact]
    public void Every_button_without_a_Tag_gets_an_icon_from_its_label()
    {
        var bare = XamlButtons().Concat(CodeButtons())
            .Where(button => button.Tag is null && button.Label is { Length: > 0 } label && FluentIcons.ForLabel(label).Length == 0)
            .Select(button => $"{button.Where} '{button.Label}'").ToList();

        Assert.True(bare.Count == 0,
            "These buttons have no icon. Give each a Tag, or teach FluentIcons.ForLabel a word of its label:\n" + string.Join("\n", bare));
    }

    [Fact]
    public void The_symbols_that_used_to_render_as_empty_boxes_now_have_icons()
    {
        Assert.Equal(FluentIcons.Power, FluentIconConverter.Resolve("🔌"));       // Power...
        Assert.Equal(FluentIcons.Registry, FluentIconConverter.Resolve("📂"));    // Registry...
        Assert.Equal(FluentIcons.Clock, FluentIconConverter.Resolve("⏰"));        // Scheduled tasks...
        Assert.Equal(FluentIcons.Processes, FluentIconConverter.Resolve("📊"));   // Processes...
        Assert.All(FluentIconConverter.KnownSymbols, symbol => Assert.Contains(FluentIconConverter.Resolve(symbol), Glyphs().Values));
    }

    [Theory]
    [InlineData("_Refresh", FluentIcons.Refresh)]
    [InlineData("_Delete selected...", FluentIcons.Delete)]
    [InlineData("Reset _password...", FluentIcons.Key)]
    [InlineData("_Enable/disable", FluentIcons.Accept)]
    [InlineData("Open-source _licenses...", FluentIcons.Document)]
    [InlineData("_Signed-in accounts...", FluentIcons.Contact)]
    [InlineData("Use _Windows default wallpaper", FluentIcons.Accept)]
    [InlineData("Windows _Update...", FluentIcons.Update)]
    [InlineData("Lo_g", FluentIcons.Page)]
    [InlineData("Quux", "")]
    [InlineData(null, "")]
    public void A_label_names_its_icon_by_the_first_word_that_says_what_the_button_does(string? label, string expected)
    {
        Assert.Equal(expected, FluentIcons.ForLabel(label));
    }

    [Fact]
    public void A_Tag_wins_over_the_label_and_a_button_with_neither_gets_no_icon()
    {
        var converter = new ButtonIconConverter();
        string Icon(object? tag, object? content) => (string)converter.Convert([tag!, content!], typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(FluentIcons.Lock, Icon("🔒", "_Refresh"));
        Assert.Equal(FluentIcons.Key, Icon(FluentIcons.Key, "_Refresh"));
        Assert.Equal(FluentIcons.Refresh, Icon(null, "_Refresh"));
        Assert.Equal("", Icon(null, "Quux"));
        Assert.Equal("", Icon(null, new object()));   // content that is not text
    }
}
