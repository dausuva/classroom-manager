// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

[Collection(nameof(LanguageSwitching))]
public sealed class BuiltInModuleLocalizationTests
{
    [Fact]
    public void Legacy_software_plural_translation_still_imports_and_formats_without_merging_builtins()
    {
        var root = Directory.CreateTempSubdirectory("legacy-software-").FullName;
        var before = Loc.Current;
        try
        {
            var source = Path.Combine(root, "old.po");
            File.WriteAllText(source, PoFile.Write([new(SoftwareInventoryPresentation.LegacyMissing, "Custom {0}", [])], Loc.Lithuanian, "Custom"));
            var languages = Path.Combine(root, "languages");
            LanguageFiles.Import(source, languages);
            Loc.Use(Loc.Lithuanian, languages, Loc.English);
            Assert.Equal("Custom 2", SoftwareInventoryPresentation.Missing(2));
            Assert.Equal("Same version on all checked PCs", ModuleLanguages.Software.Text("Same version on all checked PCs"));
            const string current = "Missing on {0} checked PC(s)";
            var identityOverride = new Translation(Loc.Lithuanian, "Custom", [new(current, current, []),
                new(SoftwareInventoryPresentation.LegacyMissing, "Old {0}", [])]);
            Loc.Use(identityOverride);
            Assert.True(identityOverride.HasExact(current));
            Assert.False(identityOverride.HasExact(null));
            Assert.False(identityOverride.HasExact("Absent"));
            Assert.Equal("Missing on 2 checked PC(s)", SoftwareInventoryPresentation.Missing(2));
        }
        finally { Loc.Use(before); Directory.Delete(root, true); }
    }

    [Fact]
    public void Reloading_and_removing_user_files_replaces_scoped_snapshots_without_mutating_old_windows()
    {
        const string key = "{0} file(s) copied, {1} already there";
        var folder = Directory.CreateTempSubdirectory("module-reload-").FullName;
        var before = Loc.Current;
        try
        {
            Loc.Use(Loc.Lithuanian, folder, Loc.English);
            var initial = ModuleLanguages.StudentWork;
            Assert.Same(initial, ModuleLanguages.StudentWork);
            var file = Path.Combine(folder, "lt-LT.po");
            File.WriteAllText(file, PoFile.Write([new(key, "First {0} / {1}", [])], Loc.Lithuanian, "Custom"));
            Loc.Use(Loc.Lithuanian, folder, Loc.English);
            var first = ModuleLanguages.StudentWork;
            Assert.NotSame(initial, first);
            Assert.Equal("First 3 / 2", first.FormatKey(key, 3, 2));
            File.WriteAllText(file, PoFile.Write([new(key, "Second {0} / {1}", [])], Loc.Lithuanian, "Custom"));
            Loc.Use(Loc.Lithuanian, folder, Loc.English);
            Assert.Equal("Second 3 / 2", ModuleLanguages.StudentWork.FormatKey(key, 3, 2));
            Assert.Equal("First 3 / 2", first.FormatKey(key, 3, 2));
            File.Delete(file);
            Loc.Use(Loc.Lithuanian, folder, Loc.English);
            Assert.Equal(initial.FormatKey(key, 3, 2), ModuleLanguages.StudentWork.FormatKey(key, 3, 2));
        }
        finally { Loc.Use(before); Directory.Delete(folder, true); }
    }

    public static IEnumerable<object[]> Domains => ModuleLocalizationSources.Modules.Where(module => BuiltInModules.Registry.Find(module.Domain) is not null).Select(module => new object[] { module.Domain, ModuleLocalizationSources.Keys(module.File, module.Extra).First() });

    [Theory]
    [MemberData(nameof(Domains))]
    public void Each_domain_has_embedded_resources_fallback_and_legacy_replacement(string domain, string key)
    {
        Assert.NotNull(ModuleLanguages.Resource(domain, "messages.pot"));
        var included = Environment.GetEnvironmentVariable("CLASSROOM_EXPECT_MODULE_CATALOGS") != "false";
        Assert.Equal(included, ModuleLanguages.Resource(domain, "lt-LT.po") is not null);
        var lithuanian = ModuleLanguages.Create(domain, Loc.BuiltIn(Loc.Lithuanian));
        Assert.Equal(0, lithuanian.RejectedCatalogs);
        if (included) Assert.NotEqual(key, lithuanian.Text(key)); else Assert.Equal(key, lithuanian.Text(key));
        Assert.Equal(key, ModuleLanguages.Create(domain, Translation.None).Text(key));
        Assert.Equal(key, ModuleLanguages.Create(domain, Loc.BuiltIn(Loc.Lithuanian), _ => null).Text(key));
        Assert.Equal(key, ModuleLanguages.Create(domain, Loc.BuiltIn(Loc.Lithuanian), _ => "invalid").Text(key));
        var folder = Directory.CreateTempSubdirectory("module-language-").FullName;
        var previous = Loc.Current;
        try
        {
            File.WriteAllText(Path.Combine(folder, "lt-LT.po"), PoFile.Write([new(key, "Custom", [])], Loc.Lithuanian, "Custom"));
            var overridden = Loc.Use(Loc.Lithuanian, folder, Loc.English);
            Assert.Equal("Custom", ModuleLanguages.Create(domain, overridden).Text(key));
            Assert.Equal("Core label", ModuleLanguages.Create(domain, overridden).Text("Core label"));
            File.WriteAllText(Path.Combine(folder, "lt-LT.po"), PoFile.Write([new("Close", "Custom core", [])], Loc.Lithuanian, "Custom"));
            overridden = Loc.Use(Loc.Lithuanian, folder, Loc.English);
            Assert.Equal(key, ModuleLanguages.Create(domain, overridden).Text(key)); // Missing overrides remain English, never built-in merged.
        }
        finally { Loc.Use(previous); Directory.Delete(folder, true); }
    }

    [Fact]
    public void Catalogs_match_presentation_sources_and_preserve_Lithuanian()
    {
        var update = Environment.GetEnvironmentVariable("CLASSROOM_UPDATE_MODULE_LOCALES") == "1";
        var prior = PoFile.Parse(File.ReadAllText(Path.Combine(StringExtractor.LocaleFolder, "lt-LT.po")))
            .Where(entry => entry.Id.Length > 0).ToDictionary(entry => entry.Id, entry => entry);
        foreach (var module in ModuleLocalizationSources.Modules)
        {
            var folder = Path.Combine(StringExtractor.LocaleFolder, "modules", module.Domain);
            var template = Path.Combine(folder, "messages.pot");
            var translated = Path.Combine(folder, "lt-LT.po");
            var keys = ModuleLocalizationSources.Keys(module.File, module.Extra);
            if (update)
            {
                var known = File.Exists(translated) ? PoFile.Parse(File.ReadAllText(translated)).Where(entry => entry.Id.Length > 0).ToDictionary(entry => entry.Id) : prior;
                Directory.CreateDirectory(folder);
                var entries = keys.Select(key => known.GetValueOrDefault(key) ?? new PoFile.Entry(key, "", ["#: ClassroomHost/" + module.File])).ToArray();
                File.WriteAllText(template, PoFile.Write(entries.Select(entry => entry with { Text = "" }), "", ""));
                File.WriteAllText(translated, PoFile.Write(entries, Loc.Lithuanian, "Lietuvių"));
                continue; // Generation is not a validation gate; run normally after reviewing translations and rebuilding resources.
            }
            Assert.Equal(keys.Order(), PoFile.Parse(File.ReadAllText(template)).Where(entry => entry.Id.Length > 0).Select(entry => entry.Id).Order());
            var translations = PoFile.Parse(File.ReadAllText(translated)).Where(entry => entry.Id.Length > 0).ToArray();
            Assert.Equal(keys.Order(), translations.Select(entry => entry.Id).Order());
            Assert.All(translations, entry => Assert.False(string.IsNullOrEmpty(entry.Text), module.Domain + ": " + entry.Id));
            var language = ScopedTranslation.Load(module.Domain, Loc.Lithuanian, culture => culture == Loc.Lithuanian ? File.ReadAllText(translated) : null);
            Assert.Equal(0, language.RejectedCatalogs);
            Assert.All(translations.Where(entry => !entry.Id.Contains('{')), entry => Assert.Equal(entry.Text, language.Text(entry.Id)));
        }
    }
}
