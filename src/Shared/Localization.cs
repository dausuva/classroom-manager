// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassroomManager.Shared;

/// <summary>
/// Reads and writes gettext PO files: the plain-text translation format every translation tool
/// (Poedit, Lokalize, Weblate...) understands. Only what this product uses is supported: one
/// <c>msgid</c> and one <c>msgstr</c> per entry, comments, and strings continued over several lines.
/// The source language is English, and the English text itself is the key.
/// </summary>
public static class PoFile
{
    public sealed record Entry(string Id, string Text, IReadOnlyList<string> Comments);

    public static List<Entry> Parse(string text)
    {
        var entries = new List<Entry>();
        var comments = new List<string>();
        StringBuilder? id = null, str = null, current = null;

        void Flush()
        {
            if (id is not null && str is not null) entries.Add(new Entry(id.ToString(), str.ToString(), comments.ToList()));
            id = str = current = null;
            comments.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith('#'))
            {
                if (str is not null) Flush();
                comments.Add(line);
                continue;
            }
            if (line.StartsWith("msgid ", StringComparison.Ordinal))
            {
                if (str is not null) Flush();
                current = id = new StringBuilder(Unquote(line[6..]));
            }
            else if (line.StartsWith("msgstr ", StringComparison.Ordinal)) current = str = new StringBuilder(Unquote(line[7..]));
            else if (line.StartsWith('"')) current?.Append(Unquote(line));
            // msgctxt, plural forms and anything else are not used by this product and are skipped.
            else current = null;
        }
        Flush();
        return entries;
    }

    static string Unquote(string quoted)
    {
        quoted = quoted.Trim();
        if (quoted.Length < 2 || quoted[0] != '"' || quoted[^1] != '"') return "";
        var result = new StringBuilder(quoted.Length);
        for (int i = 1; i < quoted.Length - 1; i++)
        {
            var c = quoted[i];
            if (c != '\\' || i + 1 >= quoted.Length - 1) { result.Append(c); continue; }
            c = quoted[++i];
            result.Append(c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => c });
        }
        return result.ToString();
    }

    static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\t", "\\t").Replace("\r", "") + "\"";

    /// <summary>One string as PO writes it: on one line, or split after each line break so long texts stay readable.</summary>
    static void AppendString(StringBuilder text, string keyword, string value)
    {
        var lines = value.Replace("\r", "").Split('\n');
        if (lines.Length == 1) { text.Append(keyword).Append(' ').Append(Quote(value)).Append('\n'); return; }
        text.Append(keyword).Append(" \"\"\n");
        for (int i = 0; i < lines.Length; i++)
        {
            var part = lines[i] + (i < lines.Length - 1 ? "\n" : "");
            if (part.Length > 0) text.Append(Quote(part).Replace("\n", "\\n")).Append('\n');
        }
    }

    public static string Write(IEnumerable<Entry> entries, string language, string languageName)
    {
        var text = new StringBuilder();
        text.Append("# Classroom Manager translation file (gettext PO).\n");
        text.Append("# msgid is the English text and must not be changed; msgstr is its translation.\n");
        text.Append("# Keep every {0}, {1}... of the English text in the translation; they stand for a number or a name.\n");
        text.Append("# An underscore marks the keyboard shortcut letter of a button (Alt+letter); put it before a letter of your choice or leave it out.\n");
        text.Append("msgid \"\"\nmsgstr \"\"\n");
        text.Append(Quote($"Language: {language}\n").Replace("\n", "\\n")).Append('\n');
        text.Append(Quote($"Language-Name: {languageName}\n").Replace("\n", "\\n")).Append('\n');
        text.Append(Quote("Content-Type: text/plain; charset=UTF-8\n").Replace("\n", "\\n")).Append('\n');
        foreach (var entry in entries.Where(entry => entry.Id.Length > 0))
        {
            text.Append('\n');
            foreach (var comment in entry.Comments) text.Append(comment).Append('\n');
            AppendString(text, "msgid", entry.Id);
            AppendString(text, "msgstr", entry.Text);
        }
        return text.ToString();
    }

    /// <summary>A header field of the file's first entry, such as "Language" or "Language-Name"; empty when absent.</summary>
    public static string Header(IEnumerable<Entry> entries, string field)
    {
        var header = entries.FirstOrDefault(entry => entry.Id.Length == 0)?.Text ?? "";
        foreach (var line in header.Split('\n'))
            if (line.StartsWith(field + ":", StringComparison.OrdinalIgnoreCase)) return line[(field.Length + 1)..].Trim();
        return "";
    }

    /// <summary>The numbered placeholders of a text, for example {0} and {1}; doubled braces are literal braces.</summary>
    public static HashSet<int> Placeholders(string text)
    {
        var found = new HashSet<int>();
        foreach (Match match in Regex.Matches(text.Replace("{{", "").Replace("}}", ""), @"\{(\d+)\}"))
            found.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        return found;
    }
}

/// <summary>
/// One language: the translations of whole texts and of texts with placeholders.
///
/// The program is written in English and a text is translated where it is shown. A text with
/// placeholders ("{0} computer(s) selected") reaches the screen with the numbers and names already
/// filled in, so such an entry is also kept as a pattern: the finished English sentence is matched
/// against it, the filled-in parts are taken out and put into the translation.
/// </summary>
public sealed class Translation
{
    /// <summary>True for a user/injected language snapshot; scoped presentation must preserve its replacement semantics.</summary>
    public bool IsOverride { get; internal set; } = true;
    sealed record Pattern(Regex Match, string Text, int LiteralLength);

    // For a text the program put together from several of its texts ("Could not refresh: " + reason):
    // the same entries without the spaces around them, findable by their first word, and the
    // patterns in a form that may stop before the end of the text.
    readonly Dictionary<string, string> _trimmed = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<string>> _byFirstWord = new(StringComparer.Ordinal);
    readonly Dictionary<char, List<Pattern>> _leading = new();

    readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _original = new(StringComparer.Ordinal);
    readonly Dictionary<char, List<Pattern>> _patterns = new();
    readonly List<Pattern> _startWithValue = new();
    readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public string Language { get; }
    public string Name { get; }
    public int Count => _exact.Count;

    /// <summary>English: nothing to look up.</summary>
    public static readonly Translation None = new("en-US", "English", []) { IsOverride = false };

    public Translation(string language, string name, IEnumerable<PoFile.Entry> entries)
    {
        Language = language;
        Name = name;
        foreach (var entry in entries)
        {
            if (entry.Id.Length == 0 || entry.Text.Length == 0) continue;
            // A translation that drops or invents a placeholder would lose a number or throw; such an entry is left in English.
            var holes = PoFile.Placeholders(entry.Id);
            if (!PoFile.Placeholders(entry.Text).IsSubsetOf(holes)) continue;
            _exact[entry.Id] = entry.Text;
            _original.TryAdd(entry.Text, entry.Id);
            var id = entry.Id.Trim();
            if (id.Length > 0 && _trimmed.TryAdd(id, entry.Text.Trim()))
            {
                // A single word is too easily somebody's own word; a phrase, or a label ending in a colon, is the program's.
                if (holes.Count == 0 && (id.Contains(' ') || id[^1] is ':' or '.'))
                {
                    var first = id.Split(' ', 2)[0];
                    if (!_byFirstWord.TryGetValue(first, out var phrases)) _byFirstWord[first] = phrases = new();
                    phrases.Add(id);
                }
                else if (holes.Count > 0 && id[0] != '{' && id[^1] != '}' && TryPattern(id, entry.Text.Trim(), wholeText: false) is { } leading)
                {
                    if (!_leading.TryGetValue(id[0], out var leadingList)) _leading[id[0]] = leadingList = new();
                    leadingList.Add(leading);
                }
            }
            if (holes.Count > 0 && TryPattern(entry.Id, entry.Text) is { } pattern)
            {
                var first = FirstLiteral(entry.Id);
                if (first is null) _startWithValue.Add(pattern);
                else
                {
                    if (!_patterns.TryGetValue(first.Value, out var list)) _patterns[first.Value] = list = new();
                    list.Add(pattern);
                }
            }
        }
        foreach (var list in _patterns.Values.Append(_startWithValue).Concat(_leading.Values)) list.Sort((a, b) => b.LiteralLength.CompareTo(a.LiteralLength));
        foreach (var list in _byFirstWord.Values) list.Sort((a, b) => b.Length.CompareTo(a.Length));
    }

    public static Translation FromPo(string po, string fallbackLanguage = "")
    {
        var entries = PoFile.Parse(po);
        var language = PoFile.Header(entries, "Language") is { Length: > 0 } code ? code : fallbackLanguage;
        var name = PoFile.Header(entries, "Language-Name") is { Length: > 0 } given ? given : DisplayName(language);
        return new Translation(language, name, entries);
    }

    /// <summary>A language's own name for itself ("lietuvių"), or the code when Windows does not know it.</summary>
    public static string DisplayName(string language)
    {
        try { return CultureInfo.GetCultureInfo(language).NativeName; }
        catch (CultureNotFoundException) { return language; }
    }

    static char? FirstLiteral(string id) => id.Length > 0 && id[0] != '{' ? id[0] : null;

    /// <summary>The pattern of a text with placeholders, or null when too little fixed text is left to recognise it by.</summary>
    static Pattern? TryPattern(string id, string text, bool wholeText = true)
    {
        var regex = new StringBuilder("^");
        int literal = 0, letters = 0;
        var seen = new HashSet<string>();
        // Doubled braces are literal braces; split on them too, so "{{0}}" is not taken for a placeholder.
        foreach (var part in Regex.Split(id, @"(\{\{|\}\}|\{\d+\})"))
        {
            if (part.Length == 0) continue;
            if (Regex.IsMatch(part, @"^\{\d+\}$"))
            {
                var group = "v" + part[1..^1];
                // The same placeholder twice must be the same text both times.
                // A value in the middle or at the end may be anything on its line (or beyond, when the text itself has line breaks).
                var any = regex.Length == 1 ? NotAcrossParts : id.Contains('\n') ? ".+?" : @"[^\n]+?";
                regex.Append(seen.Add(group) ? $"(?<{group}>{any})" : $@"\k<{group}>");
                continue;
            }
            var fixedText = part.Replace("{{", "{").Replace("}}", "}");
            regex.Append(Regex.Escape(fixedText));
            literal += fixedText.Length;
            letters += fixedText.Count(char.IsLetter);
        }
        if (letters < 3) return null;
        // As part of a longer text the pattern ends where a word ends, not necessarily where the text does.
        regex.Append(wholeText ? "$" : @"(?=\s|$)");
        return new Pattern(new Regex(regex.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), text, literal);
    }

    /// <summary>The translation of a whole text of the program, or the text itself: never a guess from parts.</summary>
    public string Exact(string? text) => text is not null && _exact.TryGetValue(text, out var translated) ? translated : text ?? "";

    /// <summary>Distinguishes an explicit identity translation from a missing entry.</summary>
    public bool HasExact(string? text) => text is not null && _exact.ContainsKey(text);

    /// <summary>The marks the program puts between parts of one line: "PC-01 — Waiting · Paused".</summary>
    static readonly string[] Separators = ["\n", " — ", " · ", "  •  ", "  |  "];

    // What a value at the very start of a pattern may not run across: otherwise "{0} offline" would take a
    // whole two-line summary that merely ends in "offline" for its value.
    const string NotAcrossParts = @"(?:(?!\n| — | · |  •  |  \|  ).)+?";

    /// <summary>The translation of a text as it is shown, or the text itself when there is none.</summary>
    public string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text) || _exact.Count == 0) return text ?? "";
        if (_exact.TryGetValue(text, out var exact)) return exact;
        if (_cache.TryGetValue(text, out var cached)) return cached;
        if (_original.ContainsKey(text) || text.Length > 4000 || !text.Any(char.IsLetter)) return text;
        // A message with its error id at the end (T-134): the message is translated on its own and the id follows in the same form.
        // The tag alone (" (error code 020001)") is left to its own pattern below.
        if (ErrorIds.TrySplit(text, out var message, out var id) && message.Length > 0) return Translate(message) + Translate(ErrorIds.Tag(id));

        var result = FromPattern(text) ?? FromParts(text) ?? (text.Contains(' ') ? FromPieces(text) : null) ?? text;
        if (_cache.Count > 20000) _cache.Clear();
        _cache[text] = result;
        return result;
    }

    string? FromPattern(string text)
    {
        var candidates = _patterns.TryGetValue(text[0], out var list) ? list.Concat(_startWithValue) : _startWithValue;
        foreach (var pattern in candidates)
        {
            Match match;
            try { match = pattern.Match.Match(text); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!match.Success) continue;
            // A filled-in part can itself be a text of the program (a state, a role); a name or a number stays as it is.
            return Fill(pattern.Text, match);
        }
        return null;
    }

    /// <summary>
    /// A line made of parts with a mark between them: each part is translated by itself, so a state
    /// such as "Waiting" is translated even though the computer's name beside it is not. Returns null
    /// when the text has no such mark or no part changed.
    /// </summary>
    string? FromParts(string text)
    {
        foreach (var separator in Separators)
        {
            if (!text.Contains(separator, StringComparison.Ordinal)) continue;
            var parts = text.Split(separator);
            bool changed = false;
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (part.Length == 0) continue;
                var translated = _exact.TryGetValue(part, out var whole) ? whole
                    : FromPattern(part) ?? FromParts(part) ?? (part.Contains(' ') ? FromPieces(part) : null);
                if (translated is null || translated == part) continue;
                parts[i] = parts[i].Replace(part, translated);
                changed = true;
            }
            if (changed) return string.Join(separator, parts);
        }
        return null;
    }

    /// <summary>
    /// Translates a text piece by piece: wherever one of the program's texts starts at the beginning
    /// of a sentence it is replaced, and whatever is not the program's (a name, a reason reported by
    /// Windows) stays as it is. Returns null when no piece was the program's.
    /// </summary>
    string? FromPieces(string text)
    {
        var result = new StringBuilder(text.Length + 16);
        bool changed = false;
        int i = 0;
        for (int guard = 0; i < text.Length && guard < 400; guard++)
        {
            int start = i;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            result.Append(text, start, i - start);
            if (i >= text.Length) break;

            if (Piece(text, i, out var translated, out var length))
            {
                result.Append(translated);
                i += length;
                changed = true;
                continue;
            }
            // Not ours: carry it over up to the end of the sentence, the clause or the line.
            int next = i;
            while (next < text.Length && text[next] != '\n' &&
                   !(text[next] is '.' or ';' or ':' && next + 1 < text.Length && char.IsWhiteSpace(text[next + 1]))) next++;
            if (next < text.Length && text[next] != '\n') next++;
            if (next == i) next++;
            result.Append(text, i, next - i);
            i = next;
        }
        if (i < text.Length) result.Append(text, i, text.Length - i);
        return changed ? result.ToString() : null;
    }

    /// <summary>The longest of the program's texts that starts at <paramref name="at"/> and ends at a word boundary.</summary>
    bool Piece(string text, int at, out string translated, out int length)
    {
        translated = "";
        length = 0;
        int word = at;
        while (word < text.Length && text[word] != ' ' && text[word] != '\n') word++;
        if (_byFirstWord.TryGetValue(text[at..word], out var phrases))
            foreach (var phrase in phrases)
            {
                int end = at + phrase.Length;
                if (end > text.Length || string.CompareOrdinal(text, at, phrase, 0, phrase.Length) != 0) continue;
                if (end < text.Length && !char.IsWhiteSpace(text[end]) && phrase[^1] != ':') continue;
                translated = _trimmed[phrase];
                length = phrase.Length;
                break;
            }
        if (_leading.TryGetValue(text[at], out var patterns))
            foreach (var pattern in patterns)
            {
                if (pattern.LiteralLength <= length) break;
                Match match;
                try { match = pattern.Match.Match(text, at, text.Length - at); }
                catch (RegexMatchTimeoutException) { continue; }
                if (!match.Success || match.Index != at || match.Length <= length) continue;
                translated = Fill(pattern.Text, match);
                length = match.Length;
                break;
            }
        return length > 0;
    }

    string Fill(string translation, Match match) => Regex.Replace(translation, @"\{\{|\}\}|\{(\d+)\}", hole => hole.Value switch
    {
        "{{" => "{",
        "}}" => "}",
        _ => match.Groups["v" + hole.Groups[1].Value] is { Success: true } group
            ? (_exact.TryGetValue(group.Value, out var inner) ? inner : group.Value) : hole.Value,
    });

    /// <summary>Translates a text whose placeholders are still open, and fills them in.</summary>
    public string Format(FormattableString text)
    {
        if (!_exact.TryGetValue(text.Format, out var translated))
        {
            // The source may carry formats ({0:0.0}); the entry is stored without them.
            var plain = Regex.Replace(text.Format, @"\{(\d+)[^}]*\}", "{$1}");
            if (!_exact.TryGetValue(plain, out translated)) return text.ToString(CultureInfo.CurrentCulture);
            var arguments = text.GetArguments();
            return Regex.Replace(translated, @"\{\{|\}\}|\{(\d+)\}", hole => hole.Value switch
            {
                "{{" => "{",
                "}}" => "}",
                _ => Regex.Match(text.Format, @"\{" + hole.Groups[1].Value + @"([^}]*)\}") is { Success: true } original
                    ? string.Format(CultureInfo.CurrentCulture, "{0" + original.Groups[1].Value + "}", arguments[int.Parse(hole.Groups[1].Value, CultureInfo.InvariantCulture)])
                    : hole.Value,
            });
        }
        return string.Format(CultureInfo.CurrentCulture, translated, text.GetArguments());
    }

    /// <summary>The English text a translation came from; the text itself when it is not a translation of a whole text.</summary>
    public string Original(string? text) =>
        text is not null && _original.TryGetValue(text, out var original) ? original : text ?? "";
}

/// <summary>
/// The language the program shows its texts in. English and Lithuanian are built in; further
/// languages are PO files a user puts into the languages folder.
/// </summary>
public static class Loc
{
    public const string English = "en-US";
    public const string Lithuanian = "lt-LT";
    /// <summary>The setting that means "the language of Windows".</summary>
    public const string Windows = "System";

    public static Translation Current { get; private set; } = Translation.None;

    public static string T(string text) => Current.Translate(text);

    public static string F(FormattableString text) => Current.Format(text);

    /// <summary>The built-in Lithuanian translation.</summary>
    public static Translation BuiltIn(string language)
    {
        if (!language.Equals(Lithuanian, StringComparison.OrdinalIgnoreCase)) return Translation.None;
        using var stream = typeof(Loc).GetTypeInfo().Assembly.GetManifestResourceStream("locale.lt-LT.po");
        if (stream is null) return Translation.None;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var translation = Translation.FromPo(reader.ReadToEnd(), Lithuanian);
        translation.IsOverride = false;
        return translation;
    }

    /// <summary>The languages on offer: the two built-in ones, then every PO file in the folder (a file for a built-in language replaces it).</summary>
    public static List<(string Language, string Name, string? File)> Available(string? userFolder)
    {
        var list = new List<(string Language, string Name, string? File)>
        {
            (English, "English", null),
            (Lithuanian, "Lietuvių", null),
        };
        if (userFolder is null || !Directory.Exists(userFolder)) return list;
        foreach (var file in Directory.GetFiles(userFolder, "*.po").OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var translation = Translation.FromPo(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file));
                if (translation.Language.Length == 0) continue;
                list.RemoveAll(known => known.Language.Equals(translation.Language, StringComparison.OrdinalIgnoreCase));
                list.Add((translation.Language, translation.Name, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>
    /// The language to use for a setting ("System" or a language code) on a computer whose Windows
    /// interface is <paramref name="windowsLanguage"/>: the exact language, else one with the same
    /// first part ("lt" for "lt-LT"), else English.
    /// </summary>
    public static string Choose(string? setting, string windowsLanguage, IEnumerable<string> available)
    {
        var wanted = string.IsNullOrWhiteSpace(setting) || setting.Equals(Windows, StringComparison.OrdinalIgnoreCase) ? windowsLanguage : setting.Trim();
        var list = available.ToList();
        static string Short(string code) => code.Split('-', '_')[0];
        return list.FirstOrDefault(code => code.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(code => Short(code).Equals(Short(wanted), StringComparison.OrdinalIgnoreCase))
            ?? English;
    }

    /// <summary>Loads and switches to the language chosen by <see cref="Choose"/>; returns it.</summary>
    public static Translation Use(string? setting, string? userFolder = null, string? windowsLanguage = null)
    {
        var available = Available(userFolder);
        var language = Choose(setting, windowsLanguage ?? CultureInfo.CurrentUICulture.Name, available.Select(entry => entry.Language));
        var file = available.First(entry => entry.Language == language).File;
        try { Current = file is not null ? Translation.FromPo(File.ReadAllText(file), language) : BuiltIn(language); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Current = BuiltIn(language); }
        return Current;
    }

    /// <summary>For tests and for switching back to English.</summary>
    public static void Use(Translation translation) => Current = translation;
}
