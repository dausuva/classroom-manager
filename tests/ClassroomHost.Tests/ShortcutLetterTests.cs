// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// A button's underlined letter (Alt+letter) only works when no other button on screen at the same
/// time uses it. A translation chooses its own letters, so each language is checked, tab by tab of
/// the main window.
/// </summary>
public class ShortcutLetterTests
{
    static readonly Regex Tab = new(@"<TabItem[^>]*Header=""([^""]+)""[^>]*>(.*?)</TabItem>", RegexOptions.Singleline);
    static readonly Regex Labelled = new(@"<(?:Button|ToggleButton|CheckBox)\b[^>]*\bContent=""([^""{][^""]*)""", RegexOptions.Singleline);

    static char? Letter(string label)
    {
        for (int i = 0; i < label.Length - 1; i++)
        {
            if (label[i] != '_') continue;
            if (label[i + 1] == '_') { i++; continue; }
            return char.ToUpperInvariant(label[i + 1]);
        }
        return null;
    }

    /// <summary>
    /// The labels that are on screen together and compete for a letter: the buttons of one tab of the
    /// main window. (The toolbars below the tabs belong to three views of which one shows at a time,
    /// and a clash there goes to the first button in reading order; they are left to the eye.)
    /// </summary>
    static List<(string Name, List<string> Labels)> Groups()
    {
        var markup = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "MainWindow.xaml"));
        return Tab.Matches(markup).Where(match => !match.Groups[2].Value.Contains("DataGrid"))
            .Select(tab => (tab.Groups[1].Value, Labelled.Matches(tab.Groups[2].Value).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToList()))
            .Where(group => group.Item2.Count > 0).ToList();
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("lt-LT")]
    public void No_two_buttons_shown_together_share_a_shortcut_letter(string language)
    {
        var translation = Loc.BuiltIn(language);
        var groups = Groups();
        Assert.True(groups.Count >= 6, $"Only {groups.Count} tabs were found in MainWindow.xaml.");

        var clashes = new List<string>();
        foreach (var (name, labels) in groups)
            foreach (var clash in labels.Select(translation.Translate).Distinct().Where(label => Letter(label) is not null)
                         .GroupBy(label => Letter(label)!.Value).Where(group => group.Count() > 1))
                clashes.Add($"{name}: Alt+{clash.Key} is used by {string.Join(" and ", clash.Select(label => $"\"{label}\""))}");
        Assert.True(clashes.Count == 0,
            $"Shortcut letters that clash in {language} (move the underscore in {(language == "en-US" ? "MainWindow.xaml" : "locale\\lt-LT.po")}):\n" + string.Join("\n", clashes.Distinct()));
    }
}
