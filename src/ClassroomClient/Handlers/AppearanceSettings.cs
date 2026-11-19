// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>One registry value as Windows stores it: a number or a text. Exactly one of them is set.</summary>
readonly record struct RawValue(int? Number, string? Text)
{
    public static RawValue Dword(int number) => new(number, null);

    public static RawValue Str(string text) => new(null, text);
}

/// <summary>Reads and writes the signed-in user's registry values; the handler is tested with a fake one.</summary>
interface IAppearanceStore
{
    RawValue? Read(string path, string name);

    void Write(string path, string name, RawValue value);

    void Delete(string path, string name);

    /// <summary>Makes what was written to this value permanent. Only a setting that is first applied for a short time (the display) needs it.</summary>
    void Persist(string path, string name) { }
}

/// <summary>What has to be told after a change for it to show: the colour change to every window, the desktop to redraw, the wallpaper to be laid out again, the taskbar to read its settings.</summary>
enum AppearanceRefresh { None, Theme, Desktop, Wallpaper, Tray }

/// <summary>Where one setting lives in the registry (under the user's own hive).</summary>
sealed record AppearanceBinding(string Path, string Name);

/// <summary>
/// One setting of the Appearance catalogue: what the Host shows (id, label, kind, choices), where it is stored, and how a value in the
/// Host's words maps to and from the values Windows stores. <see cref="ToRaw"/> returns null for a value that is not valid; an entry that is
/// null inside its result means "remove the value".
/// </summary>
sealed class AppearanceSetting
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Kind { get; init; }
    public required string Label { get; init; }
    public IReadOnlyList<(string Value, string Label)> Choices { get; init; } = [];
    public int Min { get; init; }
    public int Max { get; init; }
    public int Step { get; init; } = 1;
    public string Apply { get; init; } = AppearanceWire.ApplyNow;
    public string Risk { get; init; } = AppearanceWire.RiskReversible;
    public required IReadOnlyList<AppearanceBinding> Bindings { get; init; }
    public AppearanceRefresh Refresh { get; init; } = AppearanceRefresh.None;

    /// <summary>The first Windows build this works on. Windows 10 starts at 10240, Windows 11 at 22000.</summary>
    public int MinBuild { get; init; } = 10240;

    /// <summary>The last build that has it, or 0 for no end (a setting Windows 11 removed).</summary>
    public int MaxBuild { get; init; }

    /// <summary>The values a choice has when they depend on this computer (the display sizes); null when <see cref="Choices"/> is the whole list.</summary>
    public Func<IReadOnlyList<(string Value, string Label)>>? DynamicChoices { get; init; }

    public IReadOnlyList<(string Value, string Label)> OfferedChoices => DynamicChoices?.Invoke() ?? Choices;

    public required Func<IReadOnlyList<RawValue?>, string> Current { get; init; }
    public required Func<string, IReadOnlyList<RawValue?>?> ToRaw { get; init; }

    public bool SupportedOn(int build) => build >= MinBuild && (MaxBuild == 0 || build <= MaxBuild);
}

/// <summary>The settings this Client can change, in the order the Host shows them. Each was read on a real Windows 10 and Windows 11 PC before it was added.</summary>
static class AppearanceSettings
{
    public const string Colours = "Colours";
    public const string Desktop = "Desktop";
    public const string Taskbar = "Taskbar";
    public const string Effects = "Effects";
    public const string Display = "Display";
    internal const string DisplaySizePath = "display:size";
    internal const string DisplayOrientationPath = "display:orientation";

    /// <summary>Windows 11 starts at build 22000. Something Windows 11 added or removed is gated on this.</summary>
    const int Windows11 = 22000;

    // A pseudo registry path: a system-wide setting that Windows keeps behind SystemParametersInfo, not in a value the user's hive holds.
    internal const string AnimationsPath = "spi:clientareaanimation";
    const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";

    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const string DwmKey = @"Software\Microsoft\Windows\DWM";
    const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string DesktopIconsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    const string DesktopKey = @"Control Panel\Desktop";

    // The wallpaper fit is two text values in the user's desktop key.
    static readonly (string Value, string Label, string Style, string Tile)[] Fits =
    [
        ("fill", "Fill", "10", "0"), ("fit", "Fit", "6", "0"), ("stretch", "Stretch", "2", "0"),
        ("tile", "Tile", "0", "1"), ("center", "Centre", "0", "0"), ("span", "Span", "22", "0"),
    ];

    static readonly (string Value, string Label)[] Orientations =
        [("landscape", "Landscape"), ("portrait", "Portrait"), ("landscape-flipped", "Landscape (flipped)"), ("portrait-flipped", "Portrait (flipped)")];

    public static IReadOnlyList<AppearanceSetting> All { get; } =
    [
        Mode("colors.app-mode", "Default app mode", "AppsUseLightTheme"),
        Mode("colors.windows-mode", "Default Windows mode", "SystemUsesLightTheme"),
        Toggle("colors.transparency", Colours, "Transparency effects", PersonalizeKey, "EnableTransparency", defaultOn: true, refresh: AppearanceRefresh.Theme),
        Toggle("colors.accent-start-taskbar", Colours, "Show accent colour on Start, taskbar and action centre", PersonalizeKey, "ColorPrevalence", defaultOn: false, refresh: AppearanceRefresh.Theme),
        Toggle("colors.accent-title-bars", Colours, "Show accent colour on title bars and window borders", DwmKey, "ColorPrevalence", defaultOn: false, refresh: AppearanceRefresh.Theme),

        Toggle("desktop.show-icons", Desktop, "Show desktop icons", AdvancedKey, "HideIcons", defaultOn: true, invert: true, refresh: AppearanceRefresh.Desktop),
        DesktopIcon("desktop.icon.this-pc", "This PC on the desktop", "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", shownByDefault: false),
        DesktopIcon("desktop.icon.user-files", "User's files on the desktop", "{59031a47-3f72-44a7-89c5-5595fe6b30ee}", shownByDefault: false),
        DesktopIcon("desktop.icon.network", "Network on the desktop", "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", shownByDefault: false),
        DesktopIcon("desktop.icon.recycle-bin", "Recycle Bin on the desktop", "{645FF040-5081-101B-9F08-00AA002F954E}", shownByDefault: true),
        DesktopIcon("desktop.icon.control-panel", "Control Panel on the desktop", "{5399E694-6CE5-4D6D-8FCE-1D8870FDCBA0}", shownByDefault: false),
        WallpaperFit(),

        Search(),
        Toggle("taskbar.task-view", Taskbar, "Show the Task View button", AdvancedKey, "ShowTaskViewButton", defaultOn: true, refresh: AppearanceRefresh.Tray),
        // Windows 10 shows the seconds only after Explorer is restarted, which would close the student's folder windows, so it is not offered there.
        Toggle("taskbar.clock-seconds", Taskbar, "Show seconds in the taskbar clock", AdvancedKey, "ShowSecondsInSystemClock", defaultOn: false, refresh: AppearanceRefresh.Tray, minBuild: Windows11),
        Alignment(),

        Animations(),

        DisplaySize(),
        DisplayOrientation(),
    ];

    /// <summary>Where the display sizes come from; the tests put their own list here.</summary>
    internal static Func<IReadOnlyList<(string Value, string Label)>> DisplaySizeChoices { get; set; } = DisplayModes.Sizes;

    public static AppearanceSetting? Find(string id) => All.FirstOrDefault(setting => setting.Id == id);

    static AppearanceSetting Mode(string id, string label, string name) => new()
    {
        Id = id, Category = Colours, Kind = AppearanceWire.KindChoice, Label = label, Choices = [("light", "Light"), ("dark", "Dark")],
        Bindings = [new(PersonalizeKey, name)], Refresh = AppearanceRefresh.Theme,
        // Windows has no value until someone first chooses a mode; that is light.
        Current = raw => raw[0] is { Number: 0 } ? "dark" : "light",
        ToRaw = value => value switch { "light" => [RawValue.Dword(1)], "dark" => [RawValue.Dword(0)], _ => null },
    };

    static AppearanceSetting Toggle(string id, string category, string label, string path, string name, bool defaultOn,
        AppearanceRefresh refresh, bool invert = false, int minBuild = 10240) => new()
    {
        Id = id, Category = category, Kind = AppearanceWire.KindToggle, Label = label, Bindings = [new(path, name)], Refresh = refresh, MinBuild = minBuild,
        Current = raw => Bool(raw[0] is { Number: { } number } ? (number != 0) != invert : defaultOn),
        ToRaw = value => value switch
        {
            "true" => [RawValue.Dword(invert ? 0 : 1)],
            "false" => [RawValue.Dword(invert ? 1 : 0)],
            _ => null,
        },
    };

    // A desktop icon is hidden when its value is 1. With no value Windows shows or hides it by its own default.
    static AppearanceSetting DesktopIcon(string id, string label, string guid, bool shownByDefault) =>
        Toggle(id, Desktop, label, DesktopIconsKey, guid, defaultOn: shownByDefault, refresh: AppearanceRefresh.Desktop, invert: true);

    static AppearanceSetting WallpaperFit() => new()
    {
        Id = "desktop.wallpaper-fit", Category = Desktop, Kind = AppearanceWire.KindChoice, Label = "Wallpaper fit",
        Choices = Fits.Select(fit => (fit.Value, fit.Label)).ToArray(),
        Bindings = [new(DesktopKey, "WallpaperStyle"), new(DesktopKey, "TileWallpaper")], Refresh = AppearanceRefresh.Wallpaper,
        Current = raw =>
        {
            var style = raw[0]?.Text ?? "10";
            var tile = raw[1]?.Text ?? "0";
            return Fits.FirstOrDefault(fit => fit.Style == style && fit.Tile == tile).Value ?? "";
        },
        ToRaw = value => Fits.Where(fit => fit.Value == value).Select(fit => (IReadOnlyList<RawValue?>)[RawValue.Str(fit.Style), RawValue.Str(fit.Tile)]).FirstOrDefault(),
    };

    // The taskbar search: 0 hidden, 1 an icon, 2 a box. Windows 11 also has 3 (icon with a label), which is shown as a box here and left alone until changed.
    static AppearanceSetting Search() => new()
    {
        Id = "taskbar.search", Category = Taskbar, Kind = AppearanceWire.KindChoice, Label = "Taskbar search",
        Choices = [("hidden", "Hidden"), ("icon", "Icon only"), ("box", "Search box")],
        Bindings = [new(SearchKey, "SearchboxTaskbarMode")], Refresh = AppearanceRefresh.Tray,
        Current = raw => raw[0] switch { { Number: 0 } => "hidden", { Number: 1 } => "icon", _ => "box" },
        ToRaw = value => value switch { "hidden" => [RawValue.Dword(0)], "icon" => [RawValue.Dword(1)], "box" => [RawValue.Dword(2)], _ => null },
    };

    // Windows 11 only: where the Start button and the app buttons sit.
    static AppearanceSetting Alignment() => new()
    {
        Id = "taskbar.alignment", Category = Taskbar, Kind = AppearanceWire.KindChoice, Label = "Taskbar alignment",
        Choices = [("left", "Left"), ("center", "Centre")], MinBuild = Windows11,
        Bindings = [new(AdvancedKey, "TaskbarAl")], Refresh = AppearanceRefresh.Tray,
        Current = raw => raw[0] is { Number: 0 } ? "left" : "center",
        ToRaw = value => value switch { "left" => [RawValue.Dword(0)], "center" => [RawValue.Dword(1)], _ => null },
    };

    // Animation effects of the windows and controls: one switch Windows keeps behind SystemParametersInfo.
    static AppearanceSetting Animations() => Toggle("effects.animations", Effects, "Animation effects", AnimationsPath, "", defaultOn: true, refresh: AppearanceRefresh.None);

    // The size and orientation of the main display. Either can leave nobody a picture, so each is applied for a short time and kept only when
    // the person at the computer says so (see the handler); a mode the display does not offer is refused.
    static AppearanceSetting DisplaySize() => new()
    {
        Id = "display.size", Category = Display, Kind = AppearanceWire.KindChoice, Label = "Screen resolution", Risk = AppearanceWire.RiskConfirm,
        DynamicChoices = () => DisplaySizeChoices(), Bindings = [new(DisplaySizePath, "")],
        Current = raw => raw[0]?.Text ?? "",
        ToRaw = value => System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{3,5}x\d{3,5}$") ? [RawValue.Str(value)] : null,
    };

    static AppearanceSetting DisplayOrientation() => new()
    {
        Id = "display.orientation", Category = Display, Kind = AppearanceWire.KindChoice, Label = "Screen orientation", Risk = AppearanceWire.RiskConfirm,
        Choices = Orientations, Bindings = [new(DisplayOrientationPath, "")],
        Current = raw => raw[0] is { Number: { } number } && number >= 0 && number < Orientations.Length ? Orientations[number].Value : "landscape",
        ToRaw = value => Array.FindIndex(Orientations, item => item.Value == value) is var index and >= 0 ? [RawValue.Dword(index)] : null,
    };

    static string Bool(bool value) => value ? "true" : "false";
}
