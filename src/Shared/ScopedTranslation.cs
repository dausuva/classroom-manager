// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.Text;

namespace ClassroomManager.Shared;

/// <summary>One immutable presentation domain. Exact keys and typed values only; never a global pattern registry.</summary>
public sealed class ScopedTranslation
{
    public const int MaximumCatalogCharacters = 1_048_576;
    readonly IReadOnlyDictionary<string, string>[] _catalogs;
    public string Domain { get; }
    public string Language { get; }
    public int RejectedCatalogs { get; }

    ScopedTranslation(string domain, string language, IEnumerable<IReadOnlyDictionary<string, string>> catalogs, int rejected = 0)
    {
        if (domain.Length is 0 or > 128 || domain.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-')))
            throw new ArgumentException(nameof(domain));
        Domain = domain;
        Language = language;
        _catalogs = catalogs.ToArray();
        RejectedCatalogs = rejected;
    }

    /// <summary>The owner supplies resources for this domain only. Invalid/missing catalogs fall through to parent/English.</summary>
    public static ScopedTranslation Load(string domain, string language, Func<string, string?> resource)
    {
        var catalogs = new List<IReadOnlyDictionary<string, string>>();
        int rejected = 0;
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(language.Replace('_', '-')); }
        catch (CultureNotFoundException) { return new(domain, language, [], 1); }
        for (; culture.Name.Length > 0; culture = culture.Parent)
        {
            try
            {
                var po = resource(culture.Name);
                if (po is not null) catalogs.Add(Parse(po, culture.Name));
            }
            catch (Exception error) when (error is FormatException or ArgumentException or System.IO.IOException or UnauthorizedAccessException)
            { rejected++; }
        }
        return new(domain, language, catalogs, rejected);
    }

    /// <summary>Legacy user files replace built-ins, including English fallback for omitted entries. Values remain unmodified.</summary>
    public static ScopedTranslation FromLegacy(string domain, Translation source, IEnumerable<string> keys) =>
        new(domain, source.Language, [keys.Distinct(StringComparer.Ordinal).ToDictionary(key => key, source.Exact, StringComparer.Ordinal)]);

    public string Text(string english) => Lookup(english) ?? english;

    public string FormatKey(string english, params object?[] arguments) =>
        Format(System.Runtime.CompilerServices.FormattableStringFactory.Create(english, arguments));

    public string Format(FormattableString english)
    {
        var translated = Lookup(english.Format);
        if (translated is null)
        {
            var original = Items(english.Format);
            var plain = Rewrite(english.Format, item => "{" + item.Index.ToString(CultureInfo.InvariantCulture) + "}");
            translated = Lookup(plain);
            if (translated is not null)
            {
                if (original.GroupBy(item => item.Index).Any(group => group.Select(item => item.Token).Distinct().Count() > 1))
                    return english.ToString(CultureInfo.CurrentCulture);
                translated = Rewrite(translated, item => item.Token == "{" + item.Index.ToString(CultureInfo.InvariantCulture) + "}"
                    ? original.First(value => value.Index == item.Index).Token : item.Token);
            }
        }
        if (translated is null) return english.ToString(CultureInfo.CurrentCulture);
        try { return string.Format(CultureInfo.CurrentCulture, translated, english.GetArguments()); }
        catch (FormatException) { return english.ToString(CultureInfo.CurrentCulture); }
    }

    string? Lookup(string key)
    {
        foreach (var catalog in _catalogs)
            if (catalog.TryGetValue(key, out var value)) return value;
        return null;
    }

    static IReadOnlyDictionary<string, string> Parse(string po, string language)
    {
        if (po.Length > MaximumCatalogCharacters) throw new FormatException();
        ValidateSyntax(po);
        var entries = PoFile.Parse(po);
        if (entries.Count > 10_000 || entries.Count(entry => entry.Id.Length == 0) != 1 ||
            !PoFile.Header(entries, "Language").Replace('_', '-').Equals(language, StringComparison.OrdinalIgnoreCase)) throw new FormatException();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(entry => entry.Id.Length > 0))
        {
            if (!seen.Add(entry.Id) || entry.Id.Length > 8192 || entry.Text.Length > 8192) throw new FormatException();
            if (entry.Text.Length == 0) continue;
            // New domain files must preserve every required argument. Legacy omissions are handled only by FromLegacy.
            if (!Items(entry.Id).Select(item => item.Index).ToHashSet().SetEquals(Items(entry.Text).Select(item => item.Index))) throw new FormatException();
            result.Add(entry.Id, entry.Text);
        }
        return result;
    }

    // Validate the supported singular PO subset before using the deliberately tolerant legacy parser.
    static void ValidateSyntax(string po)
    {
        int state = 0;
        foreach (var raw in po.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                if (state == 1) throw new FormatException();
                if (state == 2) state = 0;
                continue;
            }
            if (line.StartsWith("msgid ", StringComparison.Ordinal))
            {
                if (state == 1) throw new FormatException();
                state = 1;
                line = line[6..];
            }
            else if (line.StartsWith("msgstr ", StringComparison.Ordinal))
            {
                if (state != 1) throw new FormatException();
                state = 2;
                line = line[7..];
            }
            else if (state == 0 || !line.StartsWith('"')) throw new FormatException();
            if (line.Length < 2 || line[0] != '"' || line[^1] != '"') throw new FormatException();
            for (int i = 1; i < line.Length - 1; i++)
            {
                if (line[i] == '"') throw new FormatException();
                if (line[i] != '\\') continue;
                if (++i >= line.Length - 1 || !"nrt\\\"".Contains(line[i])) throw new FormatException();
            }
        }
        if (state == 1) throw new FormatException();
    }

    sealed record Item(int Start, int Length, int Index, string Token);

    // CompositeFormat performs grammar validation; the scan records already validated argument spans, not a regex approximation.
    static IReadOnlyList<Item> Items(string format)
    {
        CompositeFormat.Parse(format);
        var result = new List<Item>();
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] is not ('{' or '}')) continue;
            if (i + 1 < format.Length && format[i + 1] == format[i]) { i++; continue; }
            if (format[i] != '{') continue;
            int start = i++, digits = i;
            while (i < format.Length && char.IsAsciiDigit(format[i])) i++;
            int index = int.Parse(format.AsSpan(digits, i - digits), CultureInfo.InvariantCulture);
            while (format[i] != '}') i++;
            result.Add(new(start, i - start + 1, index, format[start..(i + 1)]));
        }
        return result;
    }

    static string Rewrite(string format, Func<Item, string> replacement)
    {
        var result = new StringBuilder();
        int previous = 0;
        foreach (var item in Items(format))
        {
            result.Append(format, previous, item.Start - previous).Append(replacement(item));
            previous = item.Start + item.Length;
        }
        return result.Append(format, previous, format.Length - previous).ToString();
    }
}
