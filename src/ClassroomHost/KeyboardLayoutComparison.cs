// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>One keyboard layout as one computer reports it.</summary>
public sealed record ComputerKeyboardLayout(string Tip, string Language, string Name);

/// <summary>What one computer reported: its layouts in order and the default one ("" when Windows picks the first).</summary>
public sealed record ComputerKeyboard(IReadOnlyList<ComputerKeyboardLayout> Layouts, string Default)
{
    public static ComputerKeyboard FromJson(JsonObject data) => new(
        (data[KeyboardLayouts.Layouts] as JsonArray ?? []).OfType<JsonObject>()
            .Select(item => new ComputerKeyboardLayout(
                KeyboardLayouts.Normalize(item["tip"]?.GetValue<string>() ?? ""),
                item["language"]?.GetValue<string>() ?? "",
                item["name"]?.GetValue<string>() ?? ""))
            .Where(layout => KeyboardLayouts.IsTip(layout.Tip)).ToList(),
        KeyboardLayouts.Normalize(data[KeyboardLayouts.Default]?.GetValue<string>() ?? ""));

    /// <summary>The layout a student gets first: the chosen default, or else the first in the list.</summary>
    public string Effective => Default.Length > 0 ? Default : Layouts.FirstOrDefault()?.Tip ?? "";
}

/// <summary>A row of the keyboard layout comparison.</summary>
public sealed class ComparedKeyboardLayout
{
    public string Tip { get; init; } = "";
    public string Name { get; init; } = "";
    public string Language { get; init; } = "";
    public string Installed { get; init; } = "";
    public string MissingFrom { get; init; } = "";
    public string DefaultOn { get; init; } = "";
    public string InRoomDefault { get; init; } = "";
    public IReadOnlyList<ClientInfo> Computers { get; init; } = [];
}

/// <summary>The room's default layouts as the teacher saved them, and what each computer lacks or has extra.</summary>
public sealed class KeyboardRoomDefault
{
    public List<string> Layouts { get; set; } = new();
    public string Default { get; set; } = "";
}

public static class KeyboardLayoutComparison
{
    public static List<ComparedKeyboardLayout> Rows(IReadOnlyDictionary<ClientInfo, ComputerKeyboard> computers, KeyboardRoomDefault? room)
    {
        var tips = computers.Values.SelectMany(keyboard => keyboard.Layouts.Select(layout => layout.Tip))
            .Concat(room?.Layouts ?? []).Distinct(StringComparer.OrdinalIgnoreCase);
        return tips.Select(tip =>
        {
            var having = computers.Where(pair => pair.Value.Layouts.Any(layout => layout.Tip == tip)).Select(pair => pair.Key).ToList();
            var known = computers.Values.SelectMany(keyboard => keyboard.Layouts).FirstOrDefault(layout => layout.Tip == tip);
            var common = KeyboardLayouts.Common.FirstOrDefault(entry => entry.Tip == tip).Name;
            return new ComparedKeyboardLayout
            {
                Tip = tip,
                Name = known?.Name is { Length: > 0 } name && name != tip ? name : common ?? tip,
                Language = known?.Language is { Length: > 0 } language ? language : KeyboardLayouts.LanguageOf(tip),
                Installed = $"{having.Count} of {computers.Count}",
                MissingFrom = string.Join(", ", computers.Keys.Except(having).Select(client => client.Name)),
                DefaultOn = string.Join(", ", computers.Where(pair => pair.Value.Effective == tip).Select(pair => pair.Key.Name)),
                InRoomDefault = room is null || !room.Layouts.Contains(tip) ? "" : room.Default == tip ? "Yes, default" : "Yes",
                Computers = having,
            };
        }).OrderByDescending(row => row.Computers.Count).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>What one computer needs to match the room default: layouts to add, to remove (only when asked), and the default to set.</summary>
    public static (List<string> Add, List<string> Remove, string Default) Plan(ComputerKeyboard computer, KeyboardRoomDefault room, bool removeOthers)
    {
        var have = computer.Layouts.Select(layout => layout.Tip).ToList();
        var add = room.Layouts.Where(tip => !have.Contains(tip)).ToList();
        var remove = removeOthers ? have.Where(tip => !room.Layouts.Contains(tip)).ToList() : [];
        var makeDefault = room.Default.Length > 0 && computer.Effective != room.Default ? room.Default : "";
        return (add, remove, makeDefault);
    }
}
