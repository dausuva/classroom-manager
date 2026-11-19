// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.Text;

namespace ClassroomManager.Host;

/// <summary>
/// How the search boxes of the program and process lists match (T-163). Searching an installed program by
/// the name of its process ("msedge") found nothing, because only the program's display name ("Microsoft
/// Edge") was looked at.
/// </summary>
public static class FilterMatch
{
    /// <summary>
    /// True when every word of the filter is found in at least one of the fields, in any order. Case and
    /// accents do not matter ("zymeti" finds "Žymėti"). An empty filter matches everything.
    /// </summary>
    public static bool Matches(string? filter, params string?[] fields)
    {
        var words = Words(filter);
        if (words.Count == 0) return true;
        var haystack = fields.Where(field => !string.IsNullOrEmpty(field)).Select(Fold).ToList();
        return words.All(word => haystack.Any(field => field.Contains(word, StringComparison.Ordinal)));
    }

    static List<string> Words(string? filter) =>
        (filter ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(Fold).Where(word => word.Length > 0).ToList();

    /// <summary>Lower case without accents: the form that texts are compared in.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark) folded.Append(char.ToLowerInvariant(character));
        return folded.ToString().Normalize(NormalizationForm.FormC);
    }
}
