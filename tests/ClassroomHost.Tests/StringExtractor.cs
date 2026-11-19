// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ClassroomManager.Shared;

namespace ClassroomHost.Tests;

/// <summary>
/// Finds every text the program can show in its C# and XAML sources and writes the translation
/// template, <c>locale/classroom-manager.pot</c>. A text with values in it ($"{count} computers")
/// becomes one entry with numbered placeholders ("{0} computers"), and pieces joined with + become
/// the one sentence the user sees. <see cref="LocalizationTests"/> fails when the template or the
/// Lithuanian file no longer matches the sources.
/// </summary>
public static class StringExtractor
{
    /// <summary>Source folders under src\ whose texts are translated, in the order their entries appear.</summary>
    public static readonly string[] Projects = ["ClassroomHost", "Shared", "ClassroomClient", "Modules/Reporting", "Modules/BrowserInventory", "Modules/Automation", "Modules/StudentWork", "Modules/Appearance", "Modules/SoftwareInventory", "Modules/Maintenance"];

    /// <summary>Non-UI sources (T-134) and feature messages extracted into their own catalog rather than the core template.</summary>
    public static readonly string[] NotShown = ["Shared/ErrorCatalog.cs", "ClassroomHost/ErrorClassifier.cs", "ClassroomHost/AppearanceMessages.cs", "ClassroomHost/CliModules.cs", "ClassroomHost/CliAppearance.cs", "ClassroomClient/PackagedKit.cs"];

    public static IReadOnlyList<string> AppearanceKeys => FromCSharp(File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ClassroomHost", "AppearanceMessages.cs"))).ToArray();

    /// <summary>Cross-domain census only. The legacy template still uses Extract and must not merge feature catalogs.</summary>
    public static List<(string Text, List<string> Files)> ExtractAll() => Extract()
        .Concat(AppearanceKeys.Select(key => (Text: key, Files: new List<string> { "ClassroomHost/AppearanceMessages.cs" })))
        .Concat(ModuleLocalizationSources.Modules.SelectMany(module => ModuleLocalizationSources.Keys(module.File, module.Extra)
            .Select(key => (Text: key, Files: new List<string> { "ClassroomHost/" + module.File }))))
        .GroupBy(entry => entry.Text, StringComparer.Ordinal)
        .Select(group => (group.Key, group.SelectMany(entry => entry.Files).Distinct().ToList())).ToList();

    public sealed record Found(string Text, string File);

    public static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "ClassroomManager.sln"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("ClassroomManager.sln was not found above the test folder.");
    }

    public static string LocaleFolder => Path.Combine(RepositoryRoot(), "locale");

    /// <summary>Every translatable text with the files it occurs in, in a stable order.</summary>
    public static List<(string Text, List<string> Files)> Extract()
    {
        var root = Path.Combine(RepositoryRoot(), "src");
        var order = new List<string>();
        var files = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var project in Projects)
        {
            var folder = Path.Combine(root, project);
            var sources = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(path => (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) &&
                               !path.Contains(@"\obj\") && !path.Contains(@"\bin\"))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            foreach (var path in sources)
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (NotShown.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
                if (ModuleLocalizationSources.Modules.Any(module => relative == "ClassroomHost/" + module.File)) continue;
                var source = File.ReadAllText(path);
                var texts = path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ? FromXaml(source) : FromCSharp(source, clientLog: project == "ClassroomClient");
                foreach (var text in texts.Where(IsText))
                {
                    if (!files.TryGetValue(text, out var list)) { files[text] = list = new(); order.Add(text); }
                    if (!list.Contains(relative)) list.Add(relative);
                }
            }
        }
        return order.Select(text => (text, files[text])).ToList();
    }

    public static string Template() => PoFile.Write(
        Extract().Select(found => new PoFile.Entry(found.Text, "", found.Files.Take(3).Select(file => "#: " + file).ToList())), "", "");

    // ---- XAML -------------------------------------------------------------------------------------

    static readonly Regex XamlText = new(@"\b(?:Content|ToolTip|Header|Title|Text|AutomationProperties\.(?:Name|HelpText))=""([^""{][^""]*)""", RegexOptions.Compiled);

    public static IEnumerable<string> FromXaml(string source) =>
        XamlText.Matches(source).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value));

    // ---- C# ---------------------------------------------------------------------------------------

    /// <summary>
    /// The texts of a C# source. Texts joined with + are one text; a text handed to the client's own
    /// log file, used as a key in [..] or as a case label is not shown to anyone and is left out.
    /// </summary>
    public static IEnumerable<string> FromCSharp(string source, bool clientLog = false)
    {
        StringBuilder? current = null;
        int holes = 0;
        bool plus = false, skip = false;
        var before = new List<string>();   // the last few tokens in front of the text being built

        foreach (var token in Tokens(source))
        {
            if (token.Kind == TokenKind.Text)
            {
                if (current is not null && plus)
                {
                    current.Append(Renumber(token.Value, holes));
                    holes += token.Holes;
                }
                else
                {
                    if (current is not null && !skip) yield return current.ToString();
                    current = new StringBuilder(token.Value);
                    holes = token.Holes;
                    skip = before.Count > 0 && (before[^1] is "[" or "case" ||
                           clientLog && before.Count >= 4 && before[^1] == "(" && before[^2] == "Write" && before[^3] == "." && before[^4] == "Log");
                }
                plus = false;
                continue;
            }
            if (token.Value == "+" && current is not null && !plus) { plus = true; continue; }
            if (current is not null)
            {
                if (!skip) yield return current.ToString();
                current = null;
                plus = false;
            }
            before.Add(token.Value);
            if (before.Count > 6) before.RemoveAt(0);
        }
        if (current is not null && !skip) yield return current.ToString();
    }

    static string Renumber(string text, int by) =>
        by == 0 ? text : Regex.Replace(text, @"\{\{|\}\}|\{(\d+)\}", match => match.Groups[1].Success ? "{" + (int.Parse(match.Groups[1].Value) + by) + "}" : match.Value);

    enum TokenKind { Text, Other }

    readonly record struct Token(TokenKind Kind, string Value, int Holes = 0);

    static IEnumerable<Token> Tokens(string source)
    {
        int i = 0, n = source.Length;
        while (i < n)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < n && source[i + 1] == '/') { i = Next(source.IndexOf('\n', i), n); continue; }
            if (c == '/' && i + 1 < n && source[i + 1] == '*') { i = Next(source.IndexOf("*/", i, StringComparison.Ordinal), n, 2); continue; }
            if (c == '\'')
            {
                int j = i + 1;
                while (j < n && source[j] != '\'') j += source[j] == '\\' ? 2 : 1;
                i = j + 1;
                yield return new Token(TokenKind.Other, "'");
                continue;
            }

            // An optional $ and @ in either order, then the opening quote.
            int start = i;
            bool interpolated = false, verbatim = false;
            while (start < n && (source[start] == '$' || source[start] == '@'))
            {
                interpolated |= source[start] == '$';
                verbatim |= source[start] == '@';
                start++;
            }
            if (start < n && source[start] == '"' && (start == i || start - i <= 2))
            {
                if (source.AsSpan(start).StartsWith("\"\"\""))
                {
                    // A raw text literal: not used for anything shown to the user; skipped whole.
                    i = Next(source.IndexOf("\"\"\"", start + 3, StringComparison.Ordinal), n, 3);
                    yield return new Token(TokenKind.Other, "raw");
                    continue;
                }
                var text = new StringBuilder();
                int holes = 0, j = start + 1;
                while (j < n)
                {
                    char ch = source[j];
                    if (verbatim && ch == '"')
                    {
                        if (j + 1 < n && source[j + 1] == '"') { text.Append('"'); j += 2; continue; }
                        break;
                    }
                    if (!verbatim && ch == '\\' && j + 1 < n)
                    {
                        char e = source[j + 1];
                        if (e == 'u' && j + 5 < n && ushort.TryParse(source.AsSpan(j + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
                        {
                            text.Append((char)code);
                            j += 6;
                            continue;
                        }
                        text.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => e });
                        j += 2;
                        continue;
                    }
                    if (!verbatim && ch == '"') break;
                    if (interpolated && ch == '{')
                    {
                        if (j + 1 < n && source[j + 1] == '{') { text.Append("{{"); j += 2; continue; }
                        j = EndOfHole(source, j + 1);
                        text.Append('{').Append(holes++).Append('}');
                        continue;
                    }
                    if (interpolated && ch == '}' && j + 1 < n && source[j + 1] == '}') { text.Append("}}"); j += 2; continue; }
                    text.Append(ch);
                    j++;
                }
                i = j + 1;
                yield return new Token(TokenKind.Text, text.ToString(), holes);
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < n && (char.IsLetterOrDigit(source[j]) || source[j] == '_')) j++;
                yield return new Token(TokenKind.Other, source[i..j]);
                i = j;
                continue;
            }
            yield return new Token(TokenKind.Other, c.ToString());
            i++;
        }
    }

    static int Next(int found, int length, int skip = 0) => found < 0 ? length : found + skip;

    /// <summary>The index just after the } that closes an interpolation hole, stepping over nested braces, texts and characters.</summary>
    static int EndOfHole(string source, int j)
    {
        int depth = 1, n = source.Length;
        while (j < n && depth > 0)
        {
            char ch = source[j];
            if (ch == '{') depth++;
            else if (ch == '}') depth--;
            else if (ch == '"')
            {
                j++;
                while (j < n && source[j] != '"') j += source[j] == '\\' ? 2 : 1;
            }
            else if (ch == '\'')
            {
                j++;
                while (j < n && source[j] != '\'') j += source[j] == '\\' ? 2 : 1;
            }
            j++;
        }
        return j;
    }

    // ---- what counts as a text for people -------------------------------------------------------------

    static readonly Regex Word = new(@"[A-Za-z]{2,}", RegexOptions.Compiled);
    // A capitalised word, with an optional shortcut mark, plural "(s)" and ending: "_Close", "Version(s)", "Columns...".
    static readonly Regex SingleWord = new(@"^_?[A-Z][a-z]*(_?[a-z]+)*(\(s\))?( ?\.\.\.|:|\.)?$", RegexOptions.Compiled);

    /// <summary>
    /// True for a sentence, a phrase or a capitalised word such as a button label; false for keys,
    /// paths, command lines, formats and other text only the program reads.
    /// </summary>
    public static bool IsText(string text)
    {
        var value = text.Trim();
        // "{0} days", "{0} MB freed": a number and its word.
        if (Regex.IsMatch(value, @"^\{\d+\} [a-z]{3,}( [a-z]+)*\.?$")) return true;
        // "{0} of {1}": a count out of a total, shown in a table.
        if (Regex.IsMatch(value, @"^\{\d+\} of \{\d+\}$")) return true;
        // A symbol in front of a label ("👁 _Show", "↻ _Scan...") does not make it less of a text.
        var symbols = value.TakeWhile(character => !char.IsLetterOrDigit(character) && character is not ('_' or '(' or '{' or '"' or '\'' or '[' or '/' or '-' or '.' or '<' or '^' or '%' or '#' or '*' or '&' or '|' or '=' or '$')).Count();
        if (symbols > 0 && symbols < value.Length) return IsText(value[symbols..]);
        if (value.Length < 2 || value.Length > 1500 || !Word.IsMatch(value)) return false;
        if (value.Contains('\\') || value.Contains("://") || value.Contains("HKLM") || value.Contains("HKCU")) return false;
        if (value[0] is '/' or '-' or '.' or '<' or '^' or '%' or '#' or '*' or '&' or '|' or '=') return false;
        // File dialog filters, script and query fragments, and names of fonts and formats.
        if (value.Contains("|*.") || value.Contains("='") || value.StartsWith("SELECT ", StringComparison.Ordinal) ||
            Regex.IsMatch(value, @"\$[A-Za-z_(]") || value.StartsWith("Content-Type", StringComparison.Ordinal) ||
            value is "Consolas" or "Segoe UI" or "Cascadia Mono, Consolas, Lucida Console, Courier New" or
            "Programs" or "Classroom Manager.lnk" or "Classroom Manager Host") return false;
        var words = Word.Matches(value).Count;
        if (!value.Contains(' '))
            return SingleWord.IsMatch(value) && !value.Contains("__");
        // A label with a value after it ("Network: {0} ({1})") is a text even with a single word.
        if (Regex.IsMatch(value, @"^[A-Z][A-Za-z ]{2,30}: \{\d+\}")) return true;
        // Two real words at least, and letters make up most of it: not a format, a pattern or a command line.
        int letters = value.Count(char.IsLetter);
        return words >= 2 && letters * 2 >= value.Count(character => !char.IsWhiteSpace(character)) &&
               !Regex.IsMatch(value, @"^[a-z]+(\.[a-z]+)+ ") && !Regex.IsMatch(value, @"\b(exe|dll|ps1|cmd) [/-]");
    }
}
