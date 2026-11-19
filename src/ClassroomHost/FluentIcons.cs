// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

using System.Globalization;
using System.Windows.Data;

/// <summary>
/// Icon glyphs used by Host command surfaces. Each must exist in Segoe Fluent Icons (Windows 11) and
/// in Segoe MDL2 Assets (Windows 10, which has only that font); a test checks both fonts.
/// </summary>
public static class FluentIcons
{
    public const string Refresh = "\uE72C";
    public const string Message = "\uE8BD";
    public const string Lock = "\uE72E";
    public const string Unlock = "\uE785";
    public const string Send = "\uE724";
    public const string Package = "\uE7B8";
    public const string Search = "\uE721";
    public const string Add = "\uE710";
    public const string Power = "\uE7E8";
    public const string Tag = "\uE8EC";
    public const string Remove = "\uE738";
    public const string Delete = "\uE74D";
    public const string Export = "\uE72D";
    public const string Import = "\uE8B5";
    public const string Clock = "\uE823";
    public const string Back = "\uE72B";
    public const string Picture = "\uE8B9";
    public const string Color = "\uE790";
    public const string Mouse = "\uE962";
    public const string Layout = "\uE8A7";
    public const string History = "\uE81C";
    public const string Download = "\uE896";
    public const string Folder = "\uE8B7";
    public const string List = "\uE8FD";
    public const string Upload = "\uE898";
    public const string Play = "\uE768";
    public const string Globe = "\uE774";
    public const string Command = "\uE756";
    public const string Update = "\uE777";
    public const string Registry = "\uE90F";
    public const string Calendar = "\uE787";
    public const string Blocked = "\uF140";
    public const string Page = "\uE7C3";
    public const string Settings = "\uE713";
    public const string Contact = "\uE77B";
    public const string Info = "\uE946";
    public const string Help = "\uE897";
    public const string Favorite = "\uE734";
    public const string Key = "\uE8D7";
    public const string Document = "\uE8A5";
    public const string Feedback = "\uE939";
    public const string Accept = "\uE8FB";
    public const string Online = "\uECCA";
    public const string Clear = "\uE894";
    public const string Close = "\uE8BB";
    public const string Display = "\uE7F4";
    public const string Save = "\uE74E";
    public const string Mail = "\uE715";
    public const string Processes = "\uE9D9";
    public const string Cancel = "\uE711";
    public const string Up = "\uE74A";
    public const string Down = "\uE74B";
    public const string Edit = "\uE70F";
    public const string Copy = "\uE8C8";
    public const string NewFolder = "\uE8F4";
    public const string Open = "\uE8E5";
    public const string Invert = "\uE8AB";
    public const string Offline = "\uE91F";

    /// <summary>
    /// The icon for a button that names none, chosen from its label. What the button is about beats
    /// what it does ("Reset _password..." is a key, not an undo); otherwise the first word that says
    /// what it does decides ("_Delete selected..." is a delete).
    /// Empty when no word is known, and the button then shows its label alone.
    /// </summary>
    public static string ForLabel(string? label)
    {
        var words = (label ?? "").Replace("_", "").ToLowerInvariant()
            .Split([' ', '.', '/', ',', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
            if (BySubject.TryGetValue(word, out var subject)) return subject;
        foreach (var word in words)
            if (ByWord.TryGetValue(word, out var icon)) return icon;
        return "";
    }

    static readonly Dictionary<string, string> BySubject = new()
    {
        ["password"] = Key, ["key"] = Key, ["update"] = Update, ["version"] = Update, ["folder"] = NewFolder, ["columns"] = List,
        ["open-source"] = Document, ["licenses"] = Document, ["signed-in"] = Contact, ["accounts"] = Contact,
    };

    static readonly Dictionary<string, string> ByWord = new()
    {
        ["refresh"] = Refresh, ["recheck"] = Refresh, ["reload"] = Refresh, ["retry"] = Refresh, ["scan"] = Search, ["check"] = Refresh,
        ["close"] = Close, ["done"] = Accept, ["ok"] = Accept, ["cancel"] = Cancel, ["back"] = Back,
        ["delete"] = Delete, ["remove"] = Delete, ["uninstall"] = Delete, ["revoke"] = Delete, ["clear"] = Clear,
        ["add"] = Add, ["create"] = Add, ["new"] = Add,
        ["browse"] = Folder, ["open"] = Open, ["save"] = Save, ["copy"] = Copy,
        ["export"] = Export, ["import"] = Import, ["download"] = Download, ["upload"] = Upload, ["send"] = Send, ["email"] = Mail,
        ["up"] = Up, ["down"] = Down, ["default"] = Favorite,["go"] = Play, ["run"] = Play, ["start"] = Play, ["install"] = Package,
        ["apply"] = Accept, ["use"] = Accept, ["enable"] = Accept, ["select"] = Accept, ["set"] = Accept,
        ["disable"] = Blocked, ["block"] = Blocked, ["end"] = Blocked, ["stop"] = Blocked, ["pause"] = Blocked,
        ["edit"] = Edit, ["rename"] = Edit, ["change"] = Edit, ["modify"] = Edit,
        ["details"] = Info, ["review"] = Info, ["about"] = Info, ["help"] = Help, ["assistant"] = Help, ["find"] = Search, ["search"] = Search, ["filter"] = Search,
        ["reset"] = Back, ["restore"] = Back, ["undo"] = Back, ["resume"] = Play,
        ["computers"] = Display, ["operations"] = History, ["log"] = Page,
        ["feedback"] = Feedback, ["share"] = Send, ["settings"] = Settings,
        ["lock"] = Lock, ["unlock"] = Unlock, ["restart"] = Refresh, ["shut"] = Power, ["wake"] = Power,
        ["yes"] = Accept, ["no"] = Cancel, ["next"] = Play, ["continue"] = Play, ["show"] = Display, ["hide"] = Clear,
    };
}

/// <summary>Maps legacy semantic symbols to the single Fluent icon family at presentation time.</summary>
public sealed class FluentIconConverter : IValueConverter
{
    static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        ["🔄"] = FluentIcons.Refresh, ["↻"] = FluentIcons.Refresh,
        ["💬"] = FluentIcons.Message,
        ["🔒"] = FluentIcons.Lock, ["🔓"] = FluentIcons.Unlock,
        ["📨"] = FluentIcons.Send, ["📦"] = FluentIcons.Package,
        ["🔍"] = FluentIcons.Search, ["➕"] = FluentIcons.Add,
        ["⚡"] = FluentIcons.Power, ["⏻"] = FluentIcons.Power, ["🔌"] = FluentIcons.Power,
        ["📂"] = FluentIcons.Registry, ["⏰"] = FluentIcons.Clock, ["📊"] = FluentIcons.Processes,
        ["🏷"] = FluentIcons.Tag, ["✏"] = FluentIcons.Edit, ["➖"] = FluentIcons.Remove, ["🗑"] = FluentIcons.Delete,
        ["📤"] = FluentIcons.Export, ["📥"] = FluentIcons.Import,
        ["🕓"] = FluentIcons.Clock, ["🕒"] = FluentIcons.Clock,
        ["↩"] = FluentIcons.Back, ["🖼"] = FluentIcons.Picture, ["◐"] = FluentIcons.Color,
        ["🖱"] = FluentIcons.Mouse, ["▦"] = FluentIcons.Layout,
        ["📋"] = FluentIcons.History, ["📁"] = FluentIcons.Folder, ["☰"] = FluentIcons.List,
        ["⬆"] = FluentIcons.Upload, ["▶"] = FluentIcons.Play, ["🌐"] = FluentIcons.Globe,
        ["⌨"] = FluentIcons.Command, ["🚀"] = FluentIcons.Package, ["🚫"] = FluentIcons.Blocked,
        ["📝"] = FluentIcons.Page, ["⚙"] = FluentIcons.Settings, ["👤"] = FluentIcons.Contact,
        ["ℹ"] = FluentIcons.Info, ["★"] = FluentIcons.Favorite, ["🔑"] = FluentIcons.Key,
        ["§"] = FluentIcons.Document, ["✍"] = FluentIcons.Feedback, ["☑"] = FluentIcons.Accept,
        ["●"] = FluentIcons.Online, ["⬜"] = FluentIcons.Clear, ["✕"] = FluentIcons.Close,
        ["⏸"] = FluentIcons.Power, ["▷"] = FluentIcons.Play,
        ["🖥"] = FluentIcons.Display, ["📅"] = FluentIcons.Calendar, ["ⓘ"] = FluentIcons.Info, ["❓"] = FluentIcons.Help,
        ["✓"] = FluentIcons.Accept, ["‒"] = FluentIcons.Remove, ["■"] = FluentIcons.Blocked, ["⬇"] = FluentIcons.Download,
        ["📄"] = FluentIcons.Document, ["◷"] = FluentIcons.Clock,
        ["⇄"] = FluentIcons.Invert, ["○"] = FluentIcons.Offline,
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var icon = value?.ToString() ?? "";
        return Map.TryGetValue(icon, out var fluent) ? fluent : icon;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>The symbols this converter understands; anything else would be drawn as an empty box.</summary>
    public static IReadOnlyCollection<string> KnownSymbols => (IReadOnlyCollection<string>)Map.Keys;

    /// <summary>The glyph for a Tag: a known symbol's icon, or the Tag itself when it already is a glyph.</summary>
    public static string Resolve(string tag) => Map.TryGetValue(tag, out var fluent) ? fluent : tag;
}

/// <summary>
/// The icon a button shows: the one its Tag names, otherwise one chosen from its label
/// (<see cref="FluentIcons.ForLabel"/>), so that buttons built without a Tag are not left bare.
/// </summary>
public sealed class ButtonIconConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var tag = values.Length > 0 ? values[0]?.ToString() ?? "" : "";
        if (tag.Length > 0) return FluentIconConverter.Resolve(tag);
        // The icon is chosen from the English name, whatever language the button shows.
        return values.Length > 1 && values[1] is string label ? FluentIcons.ForLabel(UiTranslator.Source(label)) : "";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
