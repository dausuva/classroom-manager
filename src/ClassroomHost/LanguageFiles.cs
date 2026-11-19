// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>What became of a translation file a user added.</summary>
public sealed record ImportedLanguage(string Language, string Name, int Translated, int Total, int Skipped, string File);

/// <summary>
/// The translation files a user works with: the template of English texts to translate from, the
/// built-in Lithuanian file as an example, and the check a file gets before it is taken in.
/// </summary>
public static class LanguageFiles
{
    /// <summary>The built-in Lithuanian file, or the same texts with the translations emptied: the template for a new language.</summary>
    public static string BuiltIn(bool template)
    {
        using var stream = typeof(Loc).GetTypeInfo().Assembly.GetManifestResourceStream("locale.lt-LT.po")
            ?? throw new InvalidOperationException("The built-in translation file is missing from this build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var po = reader.ReadToEnd();
        // A combined translator export remains available for legacy user files. This never merges domains into Loc.Current.
        using var featureStream = typeof(LanguageFiles).Assembly.GetManifestResourceStream(AppearancePresentation.ResourcePrefix + Loc.Lithuanian + ".po");
        using var featureReader = featureStream is null ? null : new StreamReader(featureStream, Encoding.UTF8);
        var feature = featureReader is null ? [] : PoFile.Parse(featureReader.ReadToEnd()).Where(entry => entry.Id.Length > 0).ToList();
        var keys = typeof(AppearanceMessages).GetFields(BindingFlags.Public | BindingFlags.Static).Select(field => (string)field.GetRawConstantValue()!);
        var modules = BuiltInModules.Available.Where(module => module.Id != BuiltInModules.Appearance).SelectMany(module =>
            PoFile.Parse(ModuleLanguages.Resource(module.Id, Loc.Lithuanian + ".po") ?? "")
                .Concat(PoFile.Parse(ModuleLanguages.Resource(module.Id, "messages.pot") ?? ""))
                .Where(entry => entry.Id.Length > 0));
        var entries = PoFile.Parse(po).Where(entry => entry.Id.Length > 0).Concat(feature).Concat(modules)
            .Concat(keys.Select(key => new PoFile.Entry(key, "", []))).GroupBy(entry => entry.Id, StringComparer.Ordinal).Select(group => group.First());
        return PoFile.Write(template ? entries.Select(entry => entry with { Text = "" }) : entries,
            template ? "" : Loc.Lithuanian, template ? "" : "Lietuvių");
    }

    /// <summary>
    /// Checks a PO file and copies it into the languages folder under its language code. Throws
    /// <see cref="InvalidDataException"/> with a message for the person when it cannot be used.
    /// </summary>
    public static ImportedLanguage Import(string file, string languagesFolder)
    {
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidDataException("The file could not be read: " + ex.Message); }

        var entries = PoFile.Parse(text);
        var language = PoFile.Header(entries, "Language");
        if (language.Length == 0)
        {
            // Many editors leave the header empty; the file name ("pl-PL.po") says it then.
            language = Path.GetFileNameWithoutExtension(file);
        }
        language = language.Replace('_', '-');
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(language); }
        catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }
        if (culture.Equals(CultureInfo.InvariantCulture) || language.Any(character => !char.IsLetterOrDigit(character) && character != '-') ||
            culture.EnglishName.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"The file does not say which language it is. Set its \"Language\" line to a language code such as pl-PL or de-DE, or name the file that way (found: '{language}').");
        language = culture.Name;

        var known = PoFile.Parse(BuiltIn(template: true)).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        known.Add(SoftwareInventoryPresentation.LegacyMissing); // Accept old user files without exposing an English plural suffix in new catalogs.
        var usable = entries.Where(entry => entry.Id.Length > 0 && entry.Text.Length > 0 && known.Contains(entry.Id)).ToList();
        if (usable.Count == 0)
            throw new InvalidDataException("The file translates none of this program's texts. Start from the template saved with Languages... > Save the English texts as a template.");
        var skipped = usable.Count(entry => !PoFile.Placeholders(entry.Text).IsSubsetOf(PoFile.Placeholders(entry.Id)));

        var name = PoFile.Header(entries, "Language-Name") is { Length: > 0 } given ? given : Capitalise(culture.NativeName);
        Directory.CreateDirectory(languagesFolder);
        var target = Path.Combine(languagesFolder, language + ".po");
        // Written again rather than copied: the result always names its language and is UTF-8, whatever the editor produced.
        File.WriteAllText(target, PoFile.Write(entries.Where(entry => entry.Id.Length > 0), language, name), new UTF8Encoding(false));
        return new ImportedLanguage(language, name, usable.Count - skipped, known.Count, skipped, target);
    }

    static string Capitalise(string name) => name.Length == 0 ? name : char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..];
}
