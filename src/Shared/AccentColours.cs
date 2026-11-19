// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;

namespace ClassroomManager.Shared;

/// <summary>
/// The accent colour of Windows (the colour of buttons, links and selections), set through
/// <see cref="Cmd.WindowsPreferencesSet"/> from the 48 colours Windows offers itself, or put back to the
/// Windows default. A Client older than <see cref="RequiredVersion"/> ignores the fields, so the Host asks
/// <see cref="Supported"/> first. The registry values are worked out here, with no registry in the way, so
/// they can be tested; the Client only writes them.
/// </summary>
public static class AccentColours
{
    public const string RequiredVersion = "1.16";

    // The request.
    public const string Accent = "accent";                 // "unchanged" (or missing), "default" or "#RRGGBB" from <see cref="Palette"/>
    public const string TitleBars = "accentTitleBars";     // optional bool: show the colour on title bars and window borders
    public const string Unchanged = "unchanged";
    public const string Default = "default";

    /// <summary>The colour Windows 11 uses when nothing was chosen.</summary>
    public const string DefaultRgb = "0078D4";

    public static bool Supported(string? clientProtocolVersion) => Protocol.IsAtLeast(clientProtocolVersion, RequiredVersion);

    /// <summary>The colours of the Windows colour picker, in its order (8 to a row), with Windows' own names.</summary>
    public static readonly (string Name, string Rgb)[] Palette =
    [
        ("Yellow gold", "FFB900"), ("Gold", "FF8C00"), ("Orange bright", "F7630C"), ("Orange dark", "CA5010"),
        ("Rust", "DA3B01"), ("Pale rust", "EF6950"), ("Brick red", "D13438"), ("Mod red", "FF4343"),
        ("Pale red", "E74856"), ("Red", "E81123"), ("Rose bright", "EA005E"), ("Rose", "C30052"),
        ("Plum light", "E3008C"), ("Plum", "BF0077"), ("Orchid light", "C239B3"), ("Orchid", "9A0089"),
        ("Default blue", "0078D7"), ("Navy blue", "0063B1"), ("Purple shadow", "8E8CD8"), ("Purple shadow dark", "6B69D6"),
        ("Iris pastel", "8764B8"), ("Iris spring", "744DA9"), ("Violet red light", "B146C2"), ("Violet red", "881798"),
        ("Cool blue bright", "0099BC"), ("Cool blue", "2D7D9A"), ("Seafoam", "00B7C3"), ("Seafoam teal", "038387"),
        ("Mint light", "00B294"), ("Mint dark", "018574"), ("Turf green", "00CC6A"), ("Sport green", "10893E"),
        ("Gray", "7A7574"), ("Gray brown", "5D5A58"), ("Steel blue", "68768A"), ("Metal blue", "515C6B"),
        ("Pale moss", "567C73"), ("Moss", "486860"), ("Meadow green", "498205"), ("Green", "107C10"),
        ("Overcast", "767676"), ("Storm", "4C4A48"), ("Blue gray", "69797E"), ("Gray dark", "4A5459"),
        ("Liddy green", "647C64"), ("Sage", "525E54"), ("Camouflage desert", "847545"), ("Camouflage", "7E735F"),
    ];

    /// <summary>"#RRGGBB" or "RRGGBB" as six upper-case digits, or null when it is not a colour of the palette.</summary>
    public static string? InPalette(string? text)
    {
        var digits = (text ?? "").Trim().TrimStart('#').ToUpperInvariant();
        return Palette.Any(colour => colour.Rgb == digits) ? digits : null;
    }

    /// <summary>One registry value to write: key under HKEY_CURRENT_USER, name and value (a number or bytes).</summary>
    public sealed record RegistryValue(string Key, string Name, object Value);

    public const string ExplorerAccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    public const string DwmKey = @"Software\Microsoft\Windows\DWM";
    public const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string DesktopKey = @"Control Panel\Desktop";

    /// <summary>
    /// What to write for a colour (<paramref name="rgb"/>, six digits). Windows keeps a colour as a number
    /// 0xAABBGGRR, and a palette of eight shades (three lighter, the colour, three darker, then a spare)
    /// from which it paints the taskbar and Start. Choosing a colour switches off "pick from my picture".
    /// </summary>
    public static IReadOnlyList<RegistryValue> ValuesFor(string rgb, bool? titleBars)
    {
        var (r, g, b) = Parse(rgb);
        int Abgr(int red, int green, int blue) => unchecked((int)(0xFF000000u | (uint)(blue << 16) | (uint)(green << 8) | (uint)red));
        var values = new List<RegistryValue>
        {
            new(ExplorerAccentKey, "AccentColorMenu", Abgr(r, g, b)),
            new(ExplorerAccentKey, "StartColorMenu", Abgr(Shade(r, 0.2), Shade(g, 0.2), Shade(b, 0.2))),
            new(ExplorerAccentKey, "AccentPalette", Palette8(r, g, b)),
            new(DwmKey, "AccentColor", Abgr(r, g, b)),
            new(DwmKey, "ColorizationColor", unchecked((int)(0xC4000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b))),
            new(DesktopKey, "AutoColorization", 0),
        };
        if (titleBars is bool bars) values.Add(new(DwmKey, "ColorPrevalence", bars ? 1 : 0));
        return values;
    }

    /// <summary>The Windows default colour, with "pick from my picture" off and no colour on title bars.</summary>
    public static IReadOnlyList<RegistryValue> ValuesForDefault(bool? titleBars) =>
        ValuesFor(DefaultRgb, titleBars ?? false);

    /// <summary>The colour in the "Accent" key, as six digits, or "" when it holds none.</summary>
    public static string RgbFromAbgr(int abgr) =>
        $"{abgr & 0xFF:X2}{(abgr >> 8) & 0xFF:X2}{(abgr >> 16) & 0xFF:X2}";

    /// <summary>The name Windows gives a colour, or "" when it is not one of the 48.</summary>
    public static string NameOf(string rgb) => Palette.FirstOrDefault(colour => colour.Rgb.Equals(rgb, StringComparison.OrdinalIgnoreCase)).Name ?? "";

    /// <summary>The fields of a request, from what the teacher chose; nothing for "unchanged".</summary>
    public static void AddTo(JsonObject args, string accent, bool? titleBars)
    {
        if (accent != Unchanged) args[Accent] = accent;
        if (titleBars is bool bars) args[TitleBars] = bars;
    }

    static (int R, int G, int B) Parse(string rgb) =>
        (Convert.ToInt32(rgb[..2], 16), Convert.ToInt32(rgb[2..4], 16), Convert.ToInt32(rgb[4..6], 16));

    static int Shade(int channel, double amount) => (int)Math.Round(channel * (1 - amount));
    static int Tint(int channel, double amount) => (int)Math.Round(channel + (255 - channel) * amount);

    static byte[] Palette8(int r, int g, int b)
    {
        var bytes = new List<byte>();
        foreach (var (amount, lighten) in new[] { (0.6, true), (0.4, true), (0.2, true), (0.0, true), (0.2, false), (0.4, false), (0.6, false) })
        {
            bytes.Add((byte)(lighten ? Tint(r, amount) : Shade(r, amount)));
            bytes.Add((byte)(lighten ? Tint(g, amount) : Shade(g, amount)));
            bytes.Add((byte)(lighten ? Tint(b, amount) : Shade(b, amount)));
            bytes.Add(0);
        }
        bytes.AddRange([0x00, 0x00, 0x00, 0x00]);
        return [.. bytes];
    }
}
