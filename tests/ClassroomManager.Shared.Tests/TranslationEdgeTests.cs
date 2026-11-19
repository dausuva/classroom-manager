// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>The edges of the PO reader/writer and of <see cref="Translation"/> that the mutation test found unwatched (docs/QUALITY.md).</summary>
public sealed class TranslationEdgeTests
{
    static Translation From(params (string Id, string Text)[] pairs) =>
        new("lt-LT", "Test", pairs.Select(pair => new PoFile.Entry(pair.Id, pair.Text, [])));

    [Fact]
    public void Escapes_are_read_and_a_lone_backslash_before_the_closing_quote_is_kept()
    {
        var entries = PoFile.Parse("msgid \"a\\tb\\nc\\\\d\\\"e\"\nmsgstr \"x\\ry\"\n");
        var entry = Assert.Single(entries);
        Assert.Equal("a\tb\nc\\d\"e", entry.Id);
        Assert.Equal("x\ry", entry.Text);
    }

    [Fact]
    public void A_string_continued_over_lines_is_joined_and_comments_stay_with_their_entry()
    {
        var po = "# first\n#. note\nmsgid \"\"\n\"one \"\n\"two\"\nmsgstr \"\"\n\"vienas \"\n\"du\"\n\n# second\nmsgid \"b\"\nmsgstr \"B\"\n";
        var entries = PoFile.Parse(po);
        Assert.Equal(2, entries.Count);
        Assert.Equal("one two", entries[0].Id);
        Assert.Equal("vienas du", entries[0].Text);
        Assert.Equal(["# first", "#. note"], entries[0].Comments);
        Assert.Equal(["# second"], entries[1].Comments);
        Assert.Equal("b", entries[1].Id);
    }

    [Fact]
    public void Entries_without_a_blank_line_between_them_are_still_separate_and_unknown_keywords_are_skipped()
    {
        var po = "msgctxt \"menu\"\nmsgid \"a\"\nmsgstr \"A\"\nmsgid \"b\"\n\"x\"\nmsgstr \"B\"\n# c\nmsgid \"c\"\nmsgstr \"C\"\n";
        var entries = PoFile.Parse(po);
        Assert.Equal(["a", "bx", "c"], entries.Select(e => e.Id));
        Assert.Equal(["A", "B", "C"], entries.Select(e => e.Text));
        Assert.Equal(["# c"], entries[2].Comments);
        Assert.Empty(entries[0].Comments);
    }

    [Fact]
    public void A_msgid_without_a_msgstr_is_dropped_and_a_malformed_quote_reads_as_empty()
    {
        Assert.Empty(PoFile.Parse("msgid \"lonely\"\n"));
        var entry = Assert.Single(PoFile.Parse("msgid noquotes\nmsgstr \"x\"\n"));
        Assert.Equal("", entry.Id);
    }

    [Fact]
    public void Windows_line_endings_read_like_unix_ones()
    {
        var entry = Assert.Single(PoFile.Parse("msgid \"a\"\r\nmsgstr \"b\"\r\n"));
        Assert.Equal(("a", "b"), (entry.Id, entry.Text));
    }

    [Fact]
    public void Writing_escapes_quotes_backslashes_and_tabs_splits_lines_and_skips_the_empty_id()
    {
        var written = PoFile.Write(
            [new PoFile.Entry("", "ignored", []), new PoFile.Entry("a \"q\" \\ \t", "line1\nline2\n", ["# why"])],
            "lt-LT", "Lietuvių");
        Assert.Contains("\"Language: lt-LT\\n\"", written);
        Assert.Contains("\"Language-Name: Lietuvių\\n\"", written);
        Assert.Contains("\"Content-Type: text/plain; charset=UTF-8\\n\"", written);
        Assert.Contains("# why\nmsgid \"a \\\"q\\\" \\\\ \\t\"\n", written);
        Assert.Contains("msgstr \"\"\n\"line1\\n\"\n\"line2\\n\"\n", written);
        Assert.DoesNotContain("ignored", written);

        var back = PoFile.Parse(written);
        Assert.Equal("a \"q\" \\ \t", back.Single(e => e.Id.Length > 0).Id);
        Assert.Equal("line1\nline2\n", back.Single(e => e.Id.Length > 0).Text);
        Assert.Equal("lt-LT", PoFile.Header(back, "Language"));
        Assert.Equal("Lietuvių", PoFile.Header(back, "language-name"));
        Assert.Equal("", PoFile.Header(back, "Plural-Forms"));
        Assert.Equal("", PoFile.Header([], "Language"));
    }

    [Fact]
    public void Placeholders_are_numbers_in_braces_and_doubled_braces_are_literal()
    {
        Assert.Equal([0, 2, 10], PoFile.Placeholders("{0} of {2} and {10}").OrderBy(n => n));
        Assert.Empty(PoFile.Placeholders("{{0}} is a literal"));
        Assert.Empty(PoFile.Placeholders("{name} {} { 1 }"));
        Assert.Equal([1], PoFile.Placeholders("{{0}} {1}"));
    }

    [Fact]
    public void An_entry_that_invents_a_placeholder_is_left_in_English_but_one_that_drops_one_is_kept()
    {
        var translation = From(("Hello {0}", "Labas {0} {1}"), ("Hello {0}.", "Labas."), ("Plain", "Paprastas"));
        Assert.Equal("Hello Ann", translation.Translate("Hello Ann"));
        Assert.Equal("Labas.", translation.Exact("Hello {0}."));
        Assert.Equal(1, translation.Count - 1);
        Assert.True(translation.HasExact("Plain"));
        Assert.False(translation.HasExact("Hello {0}"));
        Assert.False(translation.HasExact(null));
    }

    [Fact]
    public void Empty_ids_and_empty_translations_are_ignored()
    {
        var translation = From(("", "x"), ("Untranslated", ""), ("Done", "Atlikta"));
        Assert.Equal(1, translation.Count);
        Assert.Equal("Untranslated", translation.Exact("Untranslated"));
        Assert.Equal("", translation.Exact(null));
        Assert.Equal("", translation.Translate(null));
        Assert.Equal("", translation.Translate(""));
    }

    [Fact]
    public void English_has_nothing_to_translate_and_a_translation_is_not_translated_again()
    {
        Assert.Equal("Anything", Translation.None.Translate("Anything"));
        Assert.False(Translation.None.IsOverride);
        var translation = From(("Save", "Išsaugoti"));
        Assert.Equal("Išsaugoti", translation.Translate("Save"));
        Assert.Equal("Išsaugoti", translation.Translate("Išsaugoti"));
        Assert.Equal("Save", translation.Original("Išsaugoti"));
        Assert.Equal("Other", translation.Original("Other"));
        Assert.Equal("", translation.Original(null));
    }

    [Fact]
    public void A_text_without_letters_or_over_4000_characters_is_returned_unchanged()
    {
        var translation = From(("Save {0}", "Išsaugoti {0}"));
        Assert.Equal("123 456", translation.Translate("123 456"));
        var longText = "Save " + new string('x', 4001);
        Assert.Equal(longText, translation.Translate(longText));
        Assert.Equal("Išsaugoti " + new string('x', 100), translation.Translate("Save " + new string('x', 100)));
    }

    [Fact]
    public void A_value_may_not_run_across_the_marks_between_parts_at_the_start_of_a_pattern()
    {
        var translation = From(("{0} offline", "{0} neprisijungęs"), ("Waiting", "Laukiama"));
        Assert.Equal("PC-01 neprisijungęs", translation.Translate("PC-01 offline"));
        // Two lines: the first is not the value of "{0} offline".
        Assert.Equal("PC-01 — Laukiama\nPC-02 neprisijungęs", translation.Translate("PC-01 — Waiting\nPC-02 offline"));
    }

    [Theory]
    [InlineData("PC-01 — Waiting", "PC-01 — Laukiama")]
    [InlineData("PC-01 · Waiting", "PC-01 · Laukiama")]
    [InlineData("PC-01  •  Waiting", "PC-01  •  Laukiama")]
    [InlineData("PC-01  |  Waiting", "PC-01  |  Laukiama")]
    [InlineData("PC-01\nWaiting", "PC-01\nLaukiama")]
    [InlineData("PC-01 — Unknown", "PC-01 — Unknown")]
    public void Each_part_of_a_line_is_translated_by_itself(string text, string expected)
    {
        var translation = From(("Waiting", "Laukiama"));
        Assert.Equal(expected, translation.Translate(text));
    }

    [Fact]
    public void Parts_keep_their_surrounding_spaces_and_empty_parts_are_left_alone()
    {
        var translation = From(("Waiting", "Laukiama"));
        Assert.Equal("A —  Laukiama  — B", translation.Translate("A —  Waiting  — B"));
        Assert.Equal(" ·  · Laukiama", translation.Translate(" ·  · Waiting"));
    }

    [Fact]
    public void A_sentence_is_translated_piece_by_piece_and_what_is_not_ours_stays()
    {
        var translation = From(("Could not refresh:", "Nepavyko atnaujinti:"), ("Computer is offline.", "Kompiuteris neprisijungęs."));
        Assert.Equal("Nepavyko atnaujinti: The RPC server is unavailable.", translation.Translate("Could not refresh: The RPC server is unavailable."));
        Assert.Equal("Kompiuteris neprisijungęs. Try later.", translation.Translate("Computer is offline. Try later."));
        Assert.Equal("Something else entirely.", translation.Translate("Something else entirely."));
    }

    [Fact]
    public void A_piece_must_end_at_a_word_boundary_and_the_longest_phrase_wins()
    {
        var translation = From(("Not found", "Nerasta"), ("Not found here", "Nerasta čia"));
        Assert.Equal("Nerasta čia now", translation.Translate("Not found here now"));
        Assert.Equal("Nerasta now", translation.Translate("Not found now"));
        Assert.Equal("Not foundation of things", translation.Translate("Not foundation of things"));
    }

    [Fact]
    public void A_single_word_is_not_treated_as_a_piece_of_somebody_elses_sentence()
    {
        var translation = From(("Name", "Pavadinimas"));
        Assert.Equal("The Name of the rose", translation.Translate("The Name of the rose"));
    }

    [Fact]
    public void The_same_placeholder_twice_must_match_the_same_text_both_times()
    {
        var translation = From(("Copy {0} to {0}", "Kopijuoti {0} į {0}"));
        Assert.Equal("Kopijuoti a į a", translation.Translate("Copy a to a"));
        Assert.Equal("Copy a to b", translation.Translate("Copy a to b"));
    }

    [Fact]
    public void A_filled_in_part_that_is_itself_a_text_of_the_program_is_translated_and_a_name_is_not()
    {
        var translation = From(("State: {0}", "Būsena: {0}"), ("Locked", "Užrakinta"));
        Assert.Equal("Būsena: Užrakinta", translation.Translate("State: Locked"));
        Assert.Equal("Būsena: PC-01", translation.Translate("State: PC-01"));
    }

    [Fact]
    public void Doubled_braces_in_an_entry_are_literal_braces_in_both_the_match_and_the_result()
    {
        var translation = From(("Use {{0}} for {0} items", "Naudokite {{0}} {0} elementams"));
        Assert.Equal("Naudokite {0} 3 elementams", translation.Translate("Use {0} for 3 items"));
    }

    [Fact]
    public void A_pattern_with_too_little_fixed_text_is_not_made()
    {
        var translation = From(("{0}: {1}", "{1}: {0}"), ("{0} ab", "{0} AB"));
        Assert.Equal("x: y", translation.Translate("x: y"));
        Assert.Equal("q ab", translation.Translate("q ab"));
    }

    [Fact]
    public void A_pattern_value_may_span_lines_only_when_the_text_itself_has_line_breaks()
    {
        var oneLine = From(("Moved {0} files", "Perkelta failų: {0}"));
        Assert.Equal("Moved a\nb files", oneLine.Translate("Moved a\nb files"));
        var twoLines = From(("Moved {0}\nfiles", "Perkelta {0}\nfailų"));
        Assert.Equal("Perkelta a\nb\nfailų", twoLines.Translate("Moved a\nb\nfiles"));
    }

    [Fact]
    public void Format_translates_fills_and_honours_format_specifiers()
    {
        var translation = From(("{0} computer(s) selected", "Pasirinkta kompiuterių: {0}"), ("Took {0} s", "Truko {0} s"), ("{0} of {1}", "{1} iš {0}"));
        int count = 3;
        Assert.Equal("Pasirinkta kompiuterių: 3", translation.Format($"{count} computer(s) selected"));
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal("Truko 1.5 s", translation.Format($"Took {1.5:0.0} s"));
            Assert.Equal("Truko 2.00 s", translation.Format($"Took {2.0:F2} s"));
            Assert.Equal("2 iš 1", translation.Format($"{1} of {2}"));
            Assert.Equal("Untranslated 7", translation.Format($"Untranslated {7}"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void Format_turns_doubled_braces_into_single_ones_and_keeps_an_unknown_hole()
    {
        var translation = From(("Use {{x}} with {0}", "Naudokite {{x}} su {0}"));
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Assert.Equal("Naudokite {x} su 4.0", translation.Format($"Use {{x}} with {4.0:0.0}"));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void A_message_with_an_error_id_is_translated_apart_from_its_id()
    {
        var translation = From(("Could not save", "Nepavyko išsaugoti"), (ErrorIds.Tag("020001"), " (klaidos kodas 020001)"));
        Assert.Equal("Nepavyko išsaugoti (klaidos kodas 020001)", translation.Translate("Could not save" + ErrorIds.Tag("020001")));
    }

    [Theory]
    [InlineData("lt-LT", "lietuvių (Lietuva)")]
    [InlineData("not a language!", "not a language!")]
    public void A_language_is_named_in_its_own_words_or_by_its_code(string code, string expected) =>
        Assert.Equal(expected, Translation.DisplayName(code));

    [Fact]
    public void FromPo_takes_the_name_and_language_from_the_header_or_the_fallback()
    {
        var withHeader = Translation.FromPo("msgid \"\"\nmsgstr \"\"\n\"Language: de-DE\\n\"\n\"Language-Name: Deutsch\\n\"\n\nmsgid \"Yes\"\nmsgstr \"Ja\"\n");
        Assert.Equal(("de-DE", "Deutsch", "Ja"), (withHeader.Language, withHeader.Name, withHeader.Translate("Yes")));
        var without = Translation.FromPo("msgid \"Yes\"\nmsgstr \"Ja\"\n", "fr-FR");
        Assert.Equal("fr-FR", without.Language);
        Assert.Equal(Translation.DisplayName("fr-FR"), without.Name);
        Assert.Equal("", Translation.FromPo("msgid \"Yes\"\nmsgstr \"Ja\"\n").Language);
    }

    [Theory]
    [InlineData("lt-LT", "lt-LT")]
    [InlineData("LT-lt", "lt-LT")]
    [InlineData("lt", "lt-LT")]
    [InlineData("lt_LT", "lt-LT")]
    [InlineData("de-DE", "en-US")]
    [InlineData("", "en-US")]
    [InlineData(null, "en-US")]
    [InlineData("  lt-LT ", "lt-LT")]
    public void Choose_prefers_the_exact_language_then_the_same_first_part_then_English(string? setting, string expected) =>
        Assert.Equal(expected, Loc.Choose(setting, "de-DE", ["en-US", "lt-LT"]));

    [Fact]
    public void Choose_takes_the_windows_language_for_System_or_blank()
    {
        Assert.Equal("lt-LT", Loc.Choose("System", "lt-LT", ["en-US", "lt-LT"]));
        Assert.Equal("lt-LT", Loc.Choose("system", "lt-LT", ["en-US", "lt-LT"]));
        Assert.Equal("lt-LT", Loc.Choose("  ", "lt-LT", ["en-US", "lt-LT"]));
        Assert.Equal("lt-LT", Loc.Choose(null, "lt", ["en-US", "lt-LT"]));
        Assert.Equal("de-AT", Loc.Choose("de-AT", "lt-LT", ["en-US", "de-AT", "de-DE"]));
        Assert.Equal("de-DE", Loc.Choose("de-CH", "lt-LT", ["en-US", "de-DE"]));
    }

    [Fact]
    public void Available_lists_the_built_in_languages_a_missing_folder_adds_nothing_and_a_bad_file_is_skipped()
    {
        Assert.Equal(["en-US", "lt-LT"], Loc.Available(null).Select(l => l.Language));
        Assert.Equal(["en-US", "lt-LT"], Loc.Available(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Select(l => l.Language));

        var folder = Directory.CreateTempSubdirectory("po-edge").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "b-german.po"), "msgid \"\"\nmsgstr \"\"\n\"Language: de-DE\\n\"\n\"Language-Name: Deutsch\\n\"\n");
            File.WriteAllText(Path.Combine(folder, "a-french.po"), "msgid \"Yes\"\nmsgstr \"Oui\"\n");
            File.WriteAllText(Path.Combine(folder, "c-nolanguage.po"), "msgid \"Yes\"\nmsgstr \"X\"\n");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "ignored");
            File.WriteAllText(Path.Combine(folder, "lt.po"), "msgid \"\"\nmsgstr \"\"\n\"Language: LT-lt\\n\"\n\"Language-Name: Mano\\n\"\n");
            var list = Loc.Available(folder);
            Assert.Equal(["en-US", "a-french", "de-DE", "c-nolanguage", "LT-lt"], list.Select(l => l.Language));
            Assert.Equal("Mano", list.Single(l => l.Language == "LT-lt").Name);
            Assert.Null(list.Single(l => l.Language == "en-US").File);
            Assert.EndsWith("b-german.po", list.Single(l => l.Language == "de-DE").File);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Use_loads_a_users_file_falls_back_to_the_built_in_and_marks_which_is_an_override()
    {
        var before = Loc.Current;
        var folder = Directory.CreateTempSubdirectory("po-use").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "de.po"), "msgid \"\"\nmsgstr \"\"\n\"Language: de-DE\\n\"\n\nmsgid \"Yes\"\nmsgstr \"Ja\"\n");
            var german = Loc.Use("de-DE", folder, "en-US");
            Assert.Equal("Ja", Loc.T("Yes"));
            Assert.True(german.IsOverride);
            Assert.Equal("Ja", Loc.F($"Yes"));

            var lithuanian = Loc.Use("lt-LT", folder, "en-US");
            Assert.Equal("lt-LT", lithuanian.Language);
            Assert.False(lithuanian.IsOverride);
            Assert.True(lithuanian.Count > 100);

            var english = Loc.Use("en-US", folder, "lt-LT");
            Assert.Same(Translation.None, english);
            Assert.Equal("Yes", Loc.T("Yes"));

            Assert.Equal("lt-LT", Loc.Use("System", null, "lt-LT").Language);
            Assert.Same(Translation.None, Loc.BuiltIn("de-DE"));
            Assert.Equal("lt-LT", Loc.BuiltIn("LT-LT").Language);
        }
        finally
        {
            Loc.Use(before);
            Directory.Delete(folder, true);
        }
    }
}
