// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// T-089: the setup window, the console setup and the installers speak Lithuanian on a Lithuanian Windows.
/// The setup texts live in deploy\SetupText.lt.psd1, keyed by the English text the scripts pass to T / Get-CmText.
/// </summary>
public sealed class SetupLanguageTests
{
    static readonly string Root = SourceRoot();
    static readonly string[] Scripts = ["Setup-Gui.ps1", "Setup.ps1", "ClassroomSetup.psm1"];

    /// <summary>The English texts the scripts translate, exactly as written inside their double quotes.</summary>
    static IEnumerable<string> TranslatedTexts() => Scripts
        .SelectMany(script => Regex.Matches(File.ReadAllText(Path.Combine(Root, "deploy", script)), @"(?:\bT|Get-CmText)\s+""((?:[^""`]|`.)*)""")
            .Select(match => match.Groups[1].Value))
        // Shown through T $a or T $Options[$i]: the action and part names the scripts compare in English.
        .Concat(["Install", "Update", "Modify", "Repair", "Uninstall", "Status", "Client - a student PC", "Host - the teacher PC"])
        .Distinct();

    static List<string> TableKeys()
    {
        var table = File.ReadAllText(Path.Combine(Root, "deploy", "SetupText.lt.psd1"));
        return Regex.Matches(table, @"^\s*""((?:[^""`]|`.)*)""\s*=", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value.Replace("`„", "„").Replace("`“", "“").Replace("`”", "”")).ToList();
    }

    [Fact]
    public void Every_text_the_setup_shows_has_a_Lithuanian_one()
    {
        var keys = TableKeys().ToHashSet(StringComparer.Ordinal);
        var missing = TranslatedTexts().Where(text => !keys.Contains(text)).ToList();
        Assert.True(missing.Count == 0, "Add these to deploy\\SetupText.lt.psd1:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void The_text_table_is_one_PowerShell_can_read()
    {
        var path = Path.Combine(Root, "deploy", "SetupText.lt.psd1");
        // Windows PowerShell reads a file without a byte-order mark as ANSI, which garbles Lithuanian letters.
        Assert.True(HasBom(path), "SetupText.lt.psd1 must be saved as UTF-8 with a byte-order mark.");
        // Its hash tables ignore case: two keys differing only in case stop the whole table from loading.
        var clashes = TableKeys().GroupBy(key => key, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        Assert.True(clashes.Count == 0, "Keys that differ only in case: " + string.Join(", ", clashes));
        // PowerShell also ends a string at a typographic quote unless a backtick stands before it.
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            Assert.DoesNotMatch(@"(?<!`)[„“”]", line);
    }

    [Fact]
    public void The_installers_offer_Lithuanian_and_can_read_their_own_letters()
    {
        var folder = Path.Combine(Root, "packaging", "windows");
        if (!Directory.Exists(folder)) return; // the public source carries no installer definitions
        Assert.True(HasBom(Path.Combine(folder, "Lithuanian.isl")));
        foreach (var script in Directory.GetFiles(folder, "*.iss"))
        {
            var text = File.ReadAllText(script);
            Assert.Contains("MessagesFile: \"compiler:Default.isl,Lithuanian.isl\"", text);
            Assert.Contains("ShowLanguageDialog=no", text);
            if (text.Any(ch => ch > 127)) Assert.True(HasBom(script), $"{Path.GetFileName(script)} has Lithuanian letters and needs a byte-order mark.");
            // A message of the installer's own is given in both languages.
            foreach (Match message in Regex.Matches(text, @"\{cm:(\w+)\}"))
            {
                Assert.Contains($"en.{message.Groups[1].Value}=", text);
                Assert.Contains($"lt.{message.Groups[1].Value}=", text);
            }
        }
    }

    static bool HasBom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    static string SourceRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "ClassroomManager.sln"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("The repository folder was not found.");
    }
}
