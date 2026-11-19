// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using Xunit;

namespace ClassroomManager.Shared.Tests;

public class TranslationTests
{
    const string Po = """
        # A translator's note.
        msgid ""
        msgstr ""
        "Language: lt-LT\n"
        "Language-Name: Lietuvių\n"

        msgid "Close"
        msgstr "Uždaryti"

        msgid "_Save"
        msgstr "Iš_saugoti"

        msgid "{0} computer(s) selected"
        msgstr "Pasirinkta kompiuterių: {0}"

        msgid "The {0} key '{1}' was added to all {2} computer(s)."
        msgstr "Raktas „{1}“ (vaidmuo: {0}) pridėtas visuose {2} kompiuteriuose."

        msgid "Teacher"
        msgstr "Mokytojas"

        msgid "Could not refresh: "
        msgstr "Nepavyko atnaujinti: "

        msgid "The client stopped answering."
        msgstr "Klientas nustojo atsakinėti."

        msgid "Saved {0}\n"
        "\n"
        "Keep the password somewhere else than the file."
        msgstr "Išsaugota {0}\n"
        "\n"
        "Slaptažodį laikykite kitur nei failą."

        msgid "{0} autorun entr{1}"
        msgstr "Automatinio paleidimo įrašų: {0}"

        msgid "Broken {0}"
        msgstr "Sugadinta {1}"

        msgid "Uses {{0}} literally: {0}"
        msgstr "Naudoja {{0}} pažodžiui: {0}"

        msgid "Untranslated"
        msgstr ""
        """;

    static Translation Lithuanian() => Translation.FromPo(Po);

    [Fact]
    public void A_file_names_its_language_and_whole_texts_are_looked_up()
    {
        var lt = Lithuanian();

        Assert.Equal(("lt-LT", "Lietuvių"), (lt.Language, lt.Name));
        Assert.Equal("Uždaryti", lt.Translate("Close"));
        Assert.Equal("Iš_saugoti", lt.Translate("_Save"));
        // No translation, an empty one, and somebody's own text all stay as they are.
        Assert.Equal("Untranslated", lt.Translate("Untranslated"));
        Assert.Equal("Open", lt.Translate("Open"));
        Assert.Equal("PC-01", lt.Translate("PC-01"));
        Assert.Equal("", lt.Translate(null));
        Assert.Equal("Close", Translation.None.Translate("Close"));
    }

    [Fact]
    public void A_finished_sentence_is_matched_to_its_text_with_placeholders()
    {
        var lt = Lithuanian();

        Assert.Equal("Pasirinkta kompiuterių: 12", lt.Translate("12 computer(s) selected"));
        // The parts move to where the translation wants them, and a part that is itself a text of the program is translated.
        Assert.Equal("Raktas „Room 12“ (vaidmuo: Mokytojas) pridėtas visuose 20 kompiuteriuose.",
            lt.Translate("The Teacher key 'Room 12' was added to all 20 computer(s)."));
        // Line breaks inside a text, and a placeholder the translation leaves out.
        Assert.Equal("Išsaugota D:\\a.cmconfig\n\nSlaptažodį laikykite kitur nei failą.",
            lt.Translate("Saved D:\\a.cmconfig\n\nKeep the password somewhere else than the file."));
        Assert.Equal("Automatinio paleidimo įrašų: 3", lt.Translate("3 autorun entries"));
        Assert.Equal("Naudoja {0} pažodžiui: x", lt.Translate("Uses {0} literally: x"));
        // A translation that uses a placeholder the English does not have is ignored rather than trusted.
        Assert.Equal("Broken thing", lt.Translate("Broken thing"));
    }

    [Fact]
    public void A_text_joined_from_pieces_is_translated_piece_by_piece()
    {
        var lt = Lithuanian();

        // The program's label, then a reason that came from elsewhere.
        Assert.Equal("Nepavyko atnaujinti: Access is denied.", lt.Translate("Could not refresh: Access is denied."));
        // Both pieces are the program's.
        Assert.Equal("Nepavyko atnaujinti: Klientas nustojo atsakinėti.", lt.Translate("Could not refresh: The client stopped answering."));
        // Parts of one line with a mark between them: the state is the program's, the computer's name is not.
        Assert.Equal("PC-01 (10.0.0.1) — Uždaryti · Nepavyko atnaujinti: x", lt.Translate("PC-01 (10.0.0.1) — Close · Could not refresh: x"));
        // A short pattern with its value in front does not swallow a longer line that only ends the same way,
        // and a text of several lines is translated line by line.
        Assert.Equal("Pasirinkta kompiuterių: 2\nPC-01 — Pasirinkta kompiuterių: 5", lt.Translate("2 computer(s) selected\nPC-01 — 5 computer(s) selected"));
        // A single word inside somebody's sentence is not touched.
        Assert.Equal("Please Close the door", lt.Translate("Please Close the door"));
        Assert.Equal("Close the door", lt.Translate("Close the door"));
    }

    [Fact]
    public void A_translation_leads_back_to_its_English_text()
    {
        var lt = Lithuanian();

        Assert.Equal("Close", lt.Original("Uždaryti"));
        Assert.Equal("_Save", lt.Original("Iš_saugoti"));
        Assert.Equal("Something else", lt.Original("Something else"));
        // What is already translated is not translated again.
        Assert.Equal("Uždaryti", lt.Translate("Uždaryti"));
        // A suggested text is translated only as a whole.
        Assert.Equal("Uždaryti", lt.Exact("Close"));
        Assert.Equal("12 computer(s) selected", lt.Exact("12 computer(s) selected"));
    }

    [Fact]
    public void A_text_with_open_placeholders_is_translated_and_then_filled_in()
    {
        var lt = Lithuanian();
        int count = 7;

        Assert.Equal("Pasirinkta kompiuterių: 7", lt.Format($"{count} computer(s) selected"));
        Assert.Equal("7 other things", lt.Format($"{count} other things"));
        Assert.Equal("7 computer(s) selected", Translation.None.Format($"{count} computer(s) selected"));
    }

    [Fact]
    public void A_file_survives_being_written_and_read_again()
    {
        var entries = PoFile.Parse(Po);
        var again = PoFile.Parse(PoFile.Write(entries, "lt-LT", "Lietuvių"));

        Assert.Equal(entries.Where(entry => entry.Id.Length > 0).Select(entry => (entry.Id, entry.Text)),
                     again.Where(entry => entry.Id.Length > 0).Select(entry => (entry.Id, entry.Text)));
        Assert.Equal("lt-LT", PoFile.Header(again, "Language"));
        Assert.Contains(again, entry => entry.Id.Contains('\n') && entry.Text.Contains('\n'));
        Assert.Contains("\\\"", PoFile.Write([new PoFile.Entry("Say \"hi\"", "Sakyk „labas“ \"x\"", [])], "lt-LT", "Lietuvių"));
        Assert.Equal([0, 2], PoFile.Placeholders("{0} of {2} and {{1}}").Order());
    }

    [Theory]
    [InlineData("System", "lt-LT", "lt-LT")]
    [InlineData("", "lt-LT", "lt-LT")]
    [InlineData(null, "lt", "lt-LT")]            // only the language is known
    [InlineData("System", "en-GB", "en-US")]
    [InlineData("System", "de-DE", "en-US")]     // no German: English
    [InlineData("lt-LT", "en-US", "lt-LT")]      // the setting wins over Windows
    [InlineData("en-US", "lt-LT", "en-US")]
    [InlineData("pl-PL", "lt-LT", "pl-PL")]      // a language the user added
    [InlineData("fr-FR", "lt-LT", "en-US")]      // a file that was removed again
    public void The_language_follows_the_setting_then_Windows_then_English(string? setting, string windows, string expected)
    {
        Assert.Equal(expected, Loc.Choose(setting, windows, ["en-US", "lt-LT", "pl-PL"]));
    }

    [Fact]
    public void A_users_file_adds_a_language_or_replaces_a_built_in_one()
    {
        var folder = Directory.CreateTempSubdirectory("cm-lang-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "pl-PL.po"), "msgid \"\"\nmsgstr \"\"\n\"Language: pl-PL\\n\"\n\nmsgid \"Close\"\nmsgstr \"Zamknij\"\n");
            File.WriteAllText(Path.Combine(folder, "mine.po"), "msgid \"\"\nmsgstr \"\"\n\"Language: lt-LT\\n\"\n\"Language-Name: Mano lietuvių\\n\"\n\nmsgid \"Close\"\nmsgstr \"Užverti\"\n");

            var available = Loc.Available(folder);
            Assert.Equal(["en-US", "lt-LT", "pl-PL"], available.Select(language => language.Language));
            Assert.Equal("Mano lietuvių", available[1].Name);

            Assert.Equal("Zamknij", Loc.Use("pl-PL", folder, "en-US").Translate("Close"));
            Assert.Equal("Užverti", Loc.Use("System", folder, "lt-LT").Translate("Close"));
            Assert.Same(Translation.None, Loc.Use("en-US", folder, "lt-LT"));
            Assert.Equal(["en-US", "lt-LT"], Loc.Available(Path.Combine(folder, "missing")).Select(language => language.Language));
        }
        finally
        {
            Loc.Use(Translation.None);
            Directory.Delete(folder, recursive: true);
        }
    }
}
