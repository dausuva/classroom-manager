// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClassroomManager.Shared;

/// <summary>
/// Keyboard layouts as Windows names them: an input method "tip" such as <c>0427:00010427</c>
/// (language 0427 = Lithuanian, layout 00010427 = Lithuanian Standard), or for an input method
/// editor <c>0411:{GUID}{GUID}</c>. The answer of <see cref="Cmd.KeyboardList"/> and the arguments of
/// <see cref="Cmd.KeyboardSet"/> use these names.
/// </summary>
public static class KeyboardLayouts
{
    // keyboard.list answer
    public const string Layouts = "layouts";     // [{ tip, language, name }] in the order of the user's language list
    public const string Default = "default";     // the tip of the default input method, "" when Windows picks the first
    // keyboard.set arguments (each optional)
    public const string Add = "add";             // tip to add
    public const string Remove = "remove";       // tip to remove; the last remaining layout is refused
    public const string MakeDefault = "default"; // tip to make the default input method (added first if missing)

    static readonly Regex TipPattern = new(@"^[0-9A-Fa-f]{4}:([0-9A-Fa-f]{8}|\{[0-9A-Fa-f-]{36}\}\{[0-9A-Fa-f-]{36}\})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsTip(string? value) => value is not null && TipPattern.IsMatch(value.Trim());

    /// <summary>Upper case, so the same layout read from two computers compares equal.</summary>
    public static string Normalize(string tip) => tip.Trim().ToUpperInvariant();

    /// <summary>The language tag of a tip's language part ("0427" gives "lt-LT"), or "" when Windows does not know it.</summary>
    public static string LanguageOf(string tip)
    {
        if (!IsTip(tip)) return "";
        try { return CultureInfo.GetCultureInfo(int.Parse(tip[..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture)).Name; }
        catch (CultureNotFoundException) { return ""; }
    }

    /// <summary>The layout id of a plain keyboard tip ("0427:00010427" gives "00010427"), "" for an input method editor.</summary>
    public static string LayoutId(string tip) =>
        IsTip(tip) && tip.Length == 13 ? tip[5..].ToUpperInvariant() : "";

    /// <summary>Layouts teachers in this region ask for most, offered in the Add list; any other tip can be typed.</summary>
    public static readonly IReadOnlyList<(string Tip, string Name)> Common =
    [
        ("0409:00000409", "English (United States) - US"),
        ("0409:00020409", "English (United States) - United States-International"),
        ("0809:00000809", "English (United Kingdom)"),
        ("0427:00010427", "Lithuanian - Lithuanian Standard"),
        ("0427:00000427", "Lithuanian - Lithuanian"),
        ("0427:00020427", "Lithuanian - Lithuanian (IBM)"),
        ("0419:00000419", "Russian"),
        ("0422:00020422", "Ukrainian (Enhanced)"),
        ("0415:00000415", "Polish (Programmers)"),
        ("0426:00010426", "Latvian (Standard)"),
        ("0425:00000425", "Estonian"),
        ("0407:00000407", "German"),
        ("040C:0000040C", "French"),
    ];
}
