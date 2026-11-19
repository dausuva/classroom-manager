// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using ClassroomManager.Host;
using ClassroomManager.Reporting;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

// L10N-1: capture legacy behavior before domain-aware lookup. These are not proposed SDK semantics.
[Collection(nameof(LanguageSwitching))]
public sealed class LocalizationMigrationCharacterizationTests
{
    [Theory]
#if REPORTING_MODULE
    [InlineData("Export inventory", "Eksportuoti inventorių")]
#endif
    [InlineData("Screen saver", "Ekrano užsklanda")]
    [InlineData("Same version on all checked PCs", "Ta pati versija visuose patikrintuose kompiuteriuose")]
    [InlineData("Running normally", "Veikia įprastai")]
    [InlineData("Scheduled actions", "Suplanuoti veiksmai")]
#if BROWSER_INVENTORY_MODULE
    [InlineData("Browser profiles were not read; their state is unknown.", "Naršyklių profiliai nenuskaityti; jų būsena nežinoma.")]
#endif
    [InlineData("3 file(s) copied, 2 already there", "Nukopijuota failų: 3, jau buvo: 2")]
    public void Built_in_presentation_keeps_current_English_and_Lithuanian_output(string english, string lithuanian)
    {
        Assert.Equal(english, Translation.None.Translate(english));
        var language = Loc.BuiltIn(Loc.Lithuanian);
        // L10N-3: same visible output, now through the owning domain instead of the legacy merged table.
        var actual = english switch
        {
            "Export inventory" => ModuleLanguages.Create(BuiltInModules.Reporting, language).Text(english),
            "Same version on all checked PCs" => ModuleLanguages.Create(BuiltInModules.Software, language).Text(english),
            "Running normally" => ModuleLanguages.Create(BuiltInModules.Maintenance, language).Text(english),
            "Scheduled actions" => ModuleLanguages.Create(BuiltInModules.Automation, language).Text(english),
            "Browser profiles were not read; their state is unknown." => ModuleLanguages.Create(BuiltInModules.Browser, language).Text(english),
            "3 file(s) copied, 2 already there" => ModuleLanguages.Create(BuiltInModules.StudentWork, language).FormatKey("{0} file(s) copied, {1} already there", 3, 2),
            _ => language.Translate(english),
        };
        Assert.Equal(lithuanian, actual);
    }

    [Fact]
    public void Inventory_CSV_schema_and_data_do_not_change_when_UI_language_changes()
    {
        var previous = Loc.Current;
        try
        {
            var report = InventoryReportingModule.CreateReport([new("pc", "Close")]);
            report.Include("pc", new(new("Windows", "x64", "Maker", "Model", "CPU", 4, 8, 8192, []), [new("Close", "1", "Publisher")]));
            Loc.Use(Translation.None);
            var english = report.Build();
            Loc.Use(Loc.BuiltIn(Loc.Lithuanian));
            var lithuanian = report.Build();
            Assert.Equal(english.HardwareRows, lithuanian.HardwareRows);
            Assert.Equal(english.SoftwareRows, lithuanian.SoftwareRows);
            Assert.Equal("Computer,Program,Version,Publisher", lithuanian.SoftwareRows[0]);
            Assert.Equal("Close,Close,1,Publisher", lithuanian.SoftwareRows[1]);
        }
        finally { Loc.Use(previous); }
    }

    [Fact]
    public void Partial_user_catalog_replaces_builtin_instead_of_merging_missing_entries()
    {
        WithLanguages(folder =>
        {
            Write(folder, "mine.po", Loc.Lithuanian, ("Close", "Custom close"));
            var translation = Loc.Use(Loc.Lithuanian, folder, Loc.English);
            Assert.Equal("Custom close", translation.Translate("Close"));
            Assert.Equal("Screen saver", translation.Translate("Screen saver"));
            Assert.Equal("Ekrano užsklanda", Loc.BuiltIn(Loc.Lithuanian).Translate("Screen saver"));
        });
    }

    [Fact]
    public void Header_identity_and_last_sorted_file_win_over_filename_for_same_language()
    {
        WithLanguages(folder =>
        {
            Write(folder, "a.po", "lt-LT", ("Close", "First"));
            Write(folder, "z.po", "LT-lt", ("Close", "Last"));
            var entry = Assert.Single(Loc.Available(folder), language => language.Language.Equals("lt-LT", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Path.Combine(folder, "z.po"), entry.File);
            Assert.Equal("Last", Loc.Use("lt-LT", folder, Loc.English).Translate("Close"));
        });
    }

    [Fact]
    public void Use_reloads_a_changed_file_and_does_not_reuse_cached_results_from_previous_instance()
    {
        WithLanguages(folder =>
        {
            Write(folder, "custom.po", "pl-PL", ("Read {0} computers", "First {0}"));
            var first = Loc.Use("pl-PL", folder, Loc.English);
            Assert.Equal("First 2", Loc.T("Read 2 computers"));
            Assert.Equal("First 2", Loc.T("Read 2 computers"));
            Write(folder, "custom.po", "pl-PL", ("Read {0} computers", "Second {0}"));
            var second = Loc.Use("pl-PL", folder, Loc.English);
            Assert.NotSame(first, second);
            Assert.Equal("Second 2", Loc.T("Read 2 computers"));
            Assert.Equal("First 2", first.Translate("Read 2 computers"));
        });
    }

    [Fact]
    public void Removed_override_falls_back_to_builtin_and_removed_custom_language_to_English()
    {
        WithLanguages(folder =>
        {
            var builtinOverride = Write(folder, "lt.po", "lt-LT", ("Close", "Custom close"));
            var custom = Write(folder, "pl.po", "pl-PL", ("Close", "Zamknij"));
            Assert.Equal("Zamknij", Loc.Use("pl-PL", folder, Loc.English).Translate("Close"));
            File.Delete(custom);
            Assert.Same(Translation.None, Loc.Use("pl-PL", folder, Loc.English));
            File.Delete(builtinOverride);
            Assert.Equal("Uždaryti", Loc.Use(Loc.Lithuanian, folder, Loc.English).Translate("Close"));
        });
    }

    [Fact]
    public void Existing_language_choice_is_same_language_prefix_not_strict_parent_chain()
    {
        Assert.Equal("pt-PT", Loc.Choose("pt-BR", Loc.English, [Loc.English, "pt-PT"]));
        Assert.Equal("lt-LT", Loc.Choose("lt_LT", Loc.English, [Loc.English, Loc.Lithuanian]));
        Assert.Equal(Loc.English, Loc.Choose("de-DE", Loc.Lithuanian, [Loc.English, Loc.Lithuanian]));
    }

    [Fact]
    public void Legacy_matching_translates_placeholder_values_but_typed_formatting_preserves_them()
    {
        var translation = new Translation("lt-LT", "Test", [new("Close", "Uždaryti", []), new("{0} installed on {1}", "{0} įdiegta {1}", [])]);
        Assert.Equal("Uždaryti įdiegta 2", translation.Translate("Close installed on 2"));
        var inventoryName = "Close";
        Assert.Equal("Close įdiegta 2", translation.Format($"{inventoryName} installed on {2}"));
        // This known difference motivates typed/domain-aware presentation; do not merge plugin pattern sets.
    }

    [Fact]
    public void Formatted_repeated_and_escaped_placeholders_keep_current_output()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var translation = new Translation("lt-LT", "Test", [new("Size {0}", "Dydis {0}", []), new("Twice {0}, {0}", "Dukart {0}, {0}", []), new("Uses {{0}}: {0}", "Naudoja {{0}}: {0}", [])]);
            Assert.Equal("Dydis 1.5", translation.Format($"Size {1.5:0.0}"));
            Assert.Equal("Dukart x, x", translation.Translate("Twice x, x"));
            Assert.Equal("Twice x, y", translation.Translate("Twice x, y"));
            Assert.Equal("Naudoja {0}: x", translation.Format($"Uses {{0}}: {"x"}"));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void Legacy_placeholder_validation_allows_omission_but_refuses_invented_indices()
    {
        var translation = new Translation("lt-LT", "Test", [new("{0} entr{1}", "Įrašai: {0}", []), new("Broken {0}", "Blogai {1}", [])]);
        Assert.Equal("Įrašai: 2", translation.Translate("2 entries"));
        Assert.Equal("Broken value", translation.Translate("Broken value"));
        Assert.Empty(PoFile.Placeholders("{0:0.0}")); // Legacy parser limitation, not validation for a new SDK.
    }

    [Fact]
    public void Two_UI_trees_translate_metadata_but_not_input_and_English_switch_does_not_reverse_existing_controls()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var previous = Loc.Current;
            try
            {
                Loc.Use(Loc.BuiltIn(Loc.Lithuanian));
                var first = new StackPanel();
                var second = new StackPanel();
                var label = new TextBlock { Text = "Screen saver" };
                var input = new TextBox { Text = "Close" };
                var button = new Button { Content = "Close" };
                first.Children.Add(label);
                first.Children.Add(input);
                second.Children.Add(button);
                Assert.True(UiTranslator.TranslateTree(first));
                Assert.True(UiTranslator.TranslateTree(second));
                Assert.Equal("Ekrano užsklanda", label.Text);
                Assert.Equal("Uždaryti", button.Content);
                Assert.Equal("Close", input.Text);
                Assert.False(UiTranslator.TranslateTree(first));
                Loc.Use(Translation.None);
                Assert.False(UiTranslator.TranslateTree(first));
                Assert.Equal("Ekrano užsklanda", label.Text); // No reverse pass currently; recreated English UI is the baseline.
                var recreated = new TextBlock { Text = "Screen saver" };
                Assert.False(UiTranslator.TranslateTree(recreated));
                Assert.Equal("Screen saver", recreated.Text);
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            finally { Loc.Use(previous); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        failure?.Throw();
    }

    static void WithLanguages(Action<string> action)
    {
        var previous = Loc.Current;
        var folder = Directory.CreateTempSubdirectory("cm-localization-baseline-").FullName;
        try { action(folder); }
        finally { Loc.Use(previous); Directory.Delete(folder, recursive: true); }
    }

    static string Write(string folder, string file, string language, params (string Key, string Text)[] entries)
    {
        var path = Path.Combine(folder, file);
        File.WriteAllText(path, PoFile.Write(entries.Select(entry => new PoFile.Entry(entry.Key, entry.Text, [])), language, language));
        return path;
    }
}
