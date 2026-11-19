// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// Keeps the translation files in step with the sources. When a text was added or changed, run the
/// tests once with the environment variable CLASSROOM_UPDATE_LOCALE=1: that rewrites the template
/// and adds the new texts, untranslated, to locale\lt-LT.po. Translate them there and the tests pass.
/// </summary>
public class LocalizationTests
{
    static string Pot => Path.Combine(StringExtractor.LocaleFolder, "classroom-manager.pot");
    static string Lithuanian => Path.Combine(StringExtractor.LocaleFolder, "lt-LT.po");

    static bool Updating => Environment.GetEnvironmentVariable("CLASSROOM_UPDATE_LOCALE") == "1";

    [Fact]
    public void Accessibility_labels_are_extracted_but_bindings_and_automation_ids_are_not()
    {
        const string markup = """
            <TextBox AutomationProperties.Name="Search &amp; find" AutomationProperties.HelpText="Type two letters" AutomationProperties.AutomationId="StableId"/>
            <TextBox AutomationProperties.Name="{Binding Computer}"/>
            """;
        Assert.Equal(new[] { "Search & find", "Type two letters" }, StringExtractor.FromXaml(markup));
    }

    [Fact]
    public void Source_template_contains_the_command_search_accessibility_name() =>
        Assert.Contains("Matching commands", StringExtractor.Extract().Select(found => found.Text));

    [Fact]
    public void The_template_holds_every_text_of_the_sources()
    {
        var template = StringExtractor.Template();
        if (Updating)
        {
            Directory.CreateDirectory(StringExtractor.LocaleFolder);
            File.WriteAllText(Pot, template);
            MergeIntoLithuanian();
        }
        Assert.True(File.Exists(Pot), "locale\\classroom-manager.pot is missing; run the tests with CLASSROOM_UPDATE_LOCALE=1.");
        var saved = PoFile.Parse(File.ReadAllText(Pot)).Select(entry => entry.Id).Where(id => id.Length > 0).ToList();
        var now = PoFile.Parse(template).Select(entry => entry.Id).Where(id => id.Length > 0).ToList();
        var added = now.Except(saved).ToList();
        var removed = saved.Except(now).ToList();
        Assert.True(added.Count == 0 && removed.Count == 0,
            $"The sources and locale\\classroom-manager.pot differ ({added.Count} new, {removed.Count} gone). Run the tests with CLASSROOM_UPDATE_LOCALE=1, then translate the new texts in locale\\lt-LT.po.\n" +
            string.Join("\n", added.Take(10).Select(text => "+ " + text)) + "\n" + string.Join("\n", removed.Take(10).Select(text => "- " + text)));
    }

    /// <summary>Adds the template's new texts to the Lithuanian file and drops the ones no longer in the sources; translations are kept.</summary>
    static void MergeIntoLithuanian()
    {
        var known = File.Exists(Lithuanian)
            ? PoFile.Parse(File.ReadAllText(Lithuanian)).Where(entry => entry.Id.Length > 0).GroupBy(entry => entry.Id).ToDictionary(group => group.Key, group => group.First().Text)
            : new Dictionary<string, string>();
        var merged = PoFile.Parse(File.ReadAllText(Pot)).Where(entry => entry.Id.Length > 0)
            .Select(entry => entry with { Text = known.GetValueOrDefault(entry.Id, "") });
        File.WriteAllText(Lithuanian, PoFile.Write(merged, Loc.Lithuanian, "Lietuvių"));
    }

    [Fact]
    public void Lithuanian_translates_every_text_and_keeps_its_placeholders()
    {
        var template = PoFile.Parse(File.ReadAllText(Pot)).Where(entry => entry.Id.Length > 0).Select(entry => entry.Id).ToList();
        var entries = PoFile.Parse(File.ReadAllText(Lithuanian));
        var translated = entries.Where(entry => entry.Id.Length > 0).ToDictionary(entry => entry.Id, entry => entry.Text);

        Assert.Equal(Loc.Lithuanian, PoFile.Header(entries, "Language"));
        var missing = template.Where(id => !translated.TryGetValue(id, out var text) || text.Length == 0).ToList();
        Assert.True(missing.Count == 0, $"{missing.Count} text(s) have no Lithuanian translation in locale\\lt-LT.po:\n" + string.Join("\n", missing.Take(15)));

        // A translation may leave a placeholder out (English plural endings such as "entr{1}"), but never use one the text does not have.
        var broken = translated.Where(pair => !PoFile.Placeholders(pair.Value).IsSubsetOf(PoFile.Placeholders(pair.Key))).Select(pair => pair.Key).ToList();
        Assert.True(broken.Count == 0, "These translations use a placeholder ({0}, {1}...) the English text does not have:\n" + string.Join("\n", broken.Take(15)));

        // A text that ends by asking for more ("...") or naming a field (":") should still do so.
        var endings = translated.Where(pair => pair.Key.EndsWith("...") != pair.Value.EndsWith("...") && !pair.Value.EndsWith("…")).Select(pair => pair.Key).ToList();
        Assert.True(endings.Count == 0, "These translations lose or add the final \"...\":\n" + string.Join("\n", endings.Take(15)));
    }

    [Fact]
    public void A_user_can_take_the_template_translate_it_and_add_the_language()
    {
        var folder = Directory.CreateTempSubdirectory("cm-languages-").FullName;
        try
        {
            // The template has every text and no translation.
            var template = PoFile.Parse(LanguageFiles.BuiltIn(template: true));
            Assert.True(template.Count(entry => entry.Id.Length > 0) > 1000);
            Assert.All(template.Where(entry => entry.Id.Length > 0), entry => Assert.Equal("", entry.Text));

            // A translator fills in two texts, one of them wrongly, and says which language it is.
            var mine = template.Where(entry => entry.Id.Length > 0).Select(entry => entry.Id switch
            {
                "Close" => entry with { Text = "Zamknij" },
                "{0} update(s) available" => entry with { Text = "Dostępne aktualizacje: {1}" },
                _ => entry,
            });
            var file = Path.Combine(folder, "translated.po");
            File.WriteAllText(file, PoFile.Write(mine, "pl_PL", ""));

            var languages = Path.Combine(folder, "languages");
            var added = LanguageFiles.Import(file, languages);

            Assert.Equal(("pl-PL", 1, 1), (added.Language, added.Translated, added.Skipped));
            Assert.True(File.Exists(Path.Combine(languages, "pl-PL.po")));
            Assert.Contains(Loc.Available(languages), language => language.Language == "pl-PL" && language.Name.Length > 0);
            var polish = Translation.FromPo(File.ReadAllText(added.File));
            Assert.Equal("Zamknij", polish.Translate("Close"));
            Assert.Equal("3 update(s) available", polish.Translate("3 update(s) available"));   // the wrong one stays English

            // A file that does not say its language, and one that is not about this program.
            File.WriteAllText(file, PoFile.Write(mine, "", ""));
            Assert.Contains("language", Assert.Throws<InvalidDataException>(() => LanguageFiles.Import(file, languages)).Message);
            var other = Path.Combine(folder, "de-DE.po");
            File.WriteAllText(other, "msgid \"Hello\"\nmsgstr \"Hallo\"\n");
            Assert.Contains("none of this program", Assert.Throws<InvalidDataException>(() => LanguageFiles.Import(other, languages)).Message);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void The_built_in_Lithuanian_is_the_file_in_the_repository()
    {
        var builtIn = Loc.BuiltIn(Loc.Lithuanian);
        Assert.Equal(Loc.Lithuanian, builtIn.Language);
        Assert.True(builtIn.Count > 1000, $"Only {builtIn.Count} texts are built in.");
        Assert.Equal("Uždaryti", builtIn.Translate("Close"));
        Assert.Same(Translation.None, Loc.BuiltIn(Loc.English));
        Assert.Same(Translation.None, Loc.BuiltIn("de-DE"));
    }
}
