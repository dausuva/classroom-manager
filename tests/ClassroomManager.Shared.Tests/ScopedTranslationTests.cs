// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class ScopedTranslationTests
{
    static string Po(string culture, params (string Key, string Value)[] entries) =>
        PoFile.Write(entries.Select(entry => new PoFile.Entry(entry.Key, entry.Value, [])), culture, culture);

    [Fact]
    public void Two_domains_with_identical_keys_do_not_modify_each_other_or_core()
    {
        var core = new Translation("lt-LT", "Core", [new("Close", "Core", [])]);
        var first = ScopedTranslation.Load("test.first", "lt-LT", language => language == "lt-LT" ? Po(language, ("Close", "First")) : null);
        var second = ScopedTranslation.Load("test.second", "lt-LT", language => language == "lt-LT" ? Po(language, ("Close", "Second")) : null);
        Assert.Equal("First", first.Text("Close"));
        Assert.Equal("Second", second.Text("Close"));
        Assert.Equal("Core", core.Translate("Close"));
        Assert.Equal("Unknown", first.Text("Unknown"));
        Assert.Equal("Not a Close sentence", first.Text("Not a Close sentence"));
    }

    [Fact]
    public void Parent_fallback_is_per_key_and_missing_resources_fall_back_to_English()
    {
        var requested = new List<string>();
        var translation = ScopedTranslation.Load("test.feature", "lt-LT", language =>
        {
            requested.Add(language);
            return language switch { "lt-LT" => Po(language, ("Close", "Regional")), "lt" => Po(language, ("Close", "Parent"), ("Other", "Kitas")), _ => null };
        });
        Assert.Equal(["lt-LT", "lt"], requested);
        Assert.Equal("Regional", translation.Text("Close"));
        Assert.Equal("Kitas", translation.Text("Other"));
        Assert.Equal("Unknown", translation.Text("Unknown"));
        Assert.Equal(0, translation.RejectedCatalogs);
    }

    [Fact]
    public void Typed_arguments_are_never_translated_or_parsed_as_format_strings()
    {
        var translation = ScopedTranslation.Load("test.feature", "lt-LT", language => language == "lt-LT"
            ? Po(language, ("Close", "Uždaryti"), ("{0} installed on {1}", "{0} įdiegta {1}")) : null);
        Assert.Equal("Close įdiegta 2", translation.FormatKey("{0} installed on {1}", "Close", 2));
        Assert.Equal("{private} įdiegta 2", translation.FormatKey("{0} installed on {1}", "{private}", 2));
        Assert.Equal("Unknown Close", translation.FormatKey("Unknown {0}", "Close"));
    }

    [Fact]
    public void Formatting_preserves_qualifiers_alignment_repetition_escaped_braces_and_newlines()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var translation = ScopedTranslation.Load("test.feature", "lt-LT", language => language == "lt-LT"
                ? Po(language, ("Size {0}", "Dydis {0}"), ("Twice {0}, {0}", "Dukart {0}, {0}"), ("Literal {{0}}: {0}\n", "Pažodžiui {{0}}: {0}\n")) : null);
            Assert.Equal("Dydis   1.5", translation.Format($"Size {1.5,5:0.0}"));
            Assert.Equal("Dukart x, x", translation.FormatKey("Twice {0}, {0}", "x"));
            Assert.Equal("Pažodžiui {0}: x\n", translation.FormatKey("Literal {{0}}: {0}\n", "x"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("Missing {0}", "Trūksta")]
    [InlineData("Missing {0}", "Blogai {1}")]
    [InlineData("Broken {0", "Blogai")]
    [InlineData("Good {0}", "Blogai {0")]
    public void Invalid_argument_contract_rejects_catalog_and_keeps_English(string key, string value)
    {
        var translation = ScopedTranslation.Load("test.feature", "lt-LT", language => language == "lt-LT" ? Po(language, (key, value)) : null);
        Assert.Equal(1, translation.RejectedCatalogs);
        Assert.Equal(key, translation.Text(key));
    }

    [Theory]
    [InlineData("msgctxt \"other\"\nmsgid \"Close\"\nmsgstr \"X\"\n")]
    [InlineData("msgid_plural \"Other\"\nmsgstr[0] \"X\"\n")]
    [InlineData("msgid \"Close\"\n")]
    [InlineData("msgid \"Close\nmsgstr \"X\"\n")]
    [InlineData("garbage")]
    public void Malformed_or_unsupported_PO_is_not_silently_accepted(string body)
    {
        var translation = ScopedTranslation.Load("test.feature", "lt-LT", language => language == "lt-LT" ? Po(language) + "\n" + body : null);
        Assert.Equal(1, translation.RejectedCatalogs);
        Assert.Equal("Close", translation.Text("Close"));
    }

    [Fact]
    public void Duplicate_mismatched_and_oversized_catalogs_are_refused()
    {
        foreach (var po in new[] { Po("lt-LT", ("Close", "A"), ("Close", "B")), Po("de-DE", ("Close", "A")), new string('x', ScopedTranslation.MaximumCatalogCharacters + 1) })
        {
            var translation = ScopedTranslation.Load("test.feature", "lt-LT", language => language == "lt-LT" ? po : null);
            Assert.Equal(1, translation.RejectedCatalogs);
            Assert.Equal("Close", translation.Text("Close"));
        }
    }

    [Fact]
    public void Invalid_regional_catalog_can_fall_through_to_valid_parent()
    {
        var translation = ScopedTranslation.Load("test.feature", "lt-LT", language => language == "lt-LT" ? "broken"
            : language == "lt" ? Po("lt", ("Close", "Tėvinis")) : null);
        Assert.Equal("Tėvinis", translation.Text("Close"));
        Assert.Equal(1, translation.RejectedCatalogs);
    }

    [Fact]
    public void Legacy_partial_catalog_retains_replacement_and_omission_semantics_without_builtins()
    {
        var legacy = Translation.FromPo(Po("lt-LT", ("Close", "Custom"), ("{0} entr{1}", "Įrašai {0}")));
        var translation = ScopedTranslation.FromLegacy("test.feature", legacy, ["Close", "Other", "{0} entr{1}"]);
        Assert.Equal("Custom", translation.Text("Close"));
        Assert.Equal("Other", translation.Text("Other"));
        Assert.Equal("Įrašai 2", translation.FormatKey("{0} entr{1}", 2, "ies"));
    }

    [Fact]
    public void Replacement_snapshot_does_not_keep_old_values_or_mutate_retained_snapshot()
    {
        var first = ScopedTranslation.Load("test.feature", "lt-LT", language => language == "lt-LT" ? Po(language, ("Close", "First")) : null);
        var second = ScopedTranslation.Load("test.feature", "lt-LT", language => language == "lt-LT" ? Po(language, ("Close", "Second")) : null);
        Assert.Equal("First", first.Text("Close"));
        Assert.Equal("Second", second.Text("Close"));
        Assert.Equal("Close", ScopedTranslation.Load("test.feature", "lt-LT", _ => null).Text("Close"));
    }

    [Fact]
    public void Invalid_translated_numeric_format_falls_back_to_source()
    {
        var translation = ScopedTranslation.Load("test.feature", "lt-LT", language => language == "lt-LT" ? Po(language, ("Number {0}", "Skaičius {0:Q}")) : null);
        Assert.Equal("Number 2", translation.FormatKey("Number {0}", 2));
    }

    [Fact]
    public void Invalid_identity_and_provider_failures_are_bounded()
    {
        Assert.Throws<ArgumentException>(() => ScopedTranslation.Load("../core", "lt-LT", _ => null));
        var invalid = ScopedTranslation.Load("test.feature", "$invalid", _ => null);
        Assert.Equal(1, invalid.RejectedCatalogs);
        Assert.Equal("Close", invalid.Text("Close"));
        var unreadable = ScopedTranslation.Load("test.feature", "lt-LT", _ => throw new System.IO.IOException());
        Assert.Equal(2, unreadable.RejectedCatalogs);
    }

    [Fact]
    public void Differently_formatted_repeated_argument_uses_English_when_plain_translation_is_ambiguous()
    {
        var translation = ScopedTranslation.Load("test.feature", "lt-LT", language => language == "lt-LT" ? Po(language, ("Values {0} / {0}", "Reikšmės {0} / {0}")) : null);
        // Real repeated index, rather than two C# interpolation argument slots.
        var value = System.Runtime.CompilerServices.FormattableStringFactory.Create("Values {0:0.0} / {0:0.00}", 1.5);
        Assert.Equal(value.ToString(CultureInfo.CurrentCulture), translation.Format(value));
    }
}
