// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClassroomManager.Appearance;

/// <summary>One value a choice setting can have.</summary>
public sealed record AppearanceChoice(string Value, string Label);

/// <summary>One setting as a computer reports it: what it is, how to draw it, its current value, and whether Classroom Manager changed it.</summary>
public sealed record AppearanceSettingInfo(string Id, string Category, string Kind, string Label, IReadOnlyList<AppearanceChoice> Choices,
    int Min, int Max, int Step, string Apply, string Risk, string Value, bool Changed);

/// <summary>What one computer answered to <c>appearance.catalog</c>: its Windows version and the settings it supports.</summary>
public sealed record AppearanceComputerCatalogue(string Windows, int Build, IReadOnlyList<AppearanceSettingInfo> Settings);

/// <summary>The result of one setting in an <c>appearance.set</c> or <c>appearance.reset</c> answer.</summary>
/// <param name="ConfirmSeconds">Above 0 when the change is waiting for the person at the computer to keep it, and goes back by itself after that many seconds.</param>
public sealed record AppearanceResult(string Id, string Status, string Reason, int ConfirmSeconds = 0);

/// <summary>Reads the Client's answers. A setting that is malformed is left out, never guessed.</summary>
public static class AppearanceWireReader
{
    public const string KindToggle = "toggle";
    public const string KindChoice = "choice";
    public const string KindNumber = "number";
    public const string KindColour = "colour";

    public static AppearanceComputerCatalogue ReadCatalogue(string answer)
    {
        var data = JsonNode.Parse(answer) as JsonObject ?? [];
        var settings = (data["settings"] as JsonArray ?? []).Select(ReadSetting).OfType<AppearanceSettingInfo>().ToList();
        return new(Text(data, "windows"), Number(data, "build"), settings);
    }

    public static IReadOnlyList<AppearanceResult> ReadResults(string answer) =>
        ((JsonNode.Parse(answer) as JsonObject)?["results"] as JsonArray ?? []).OfType<JsonObject>()
        .Select(item => new AppearanceResult(Text(item, "id"), Text(item, "status"), Text(item, "reason"), Number(item, "confirmSeconds"))).Where(result => result.Id.Length > 0).ToList();

    static AppearanceSettingInfo? ReadSetting(JsonNode? node)
    {
        if (node is not JsonObject item) return null;
        var id = Text(item, "id");
        var kind = Text(item, "kind");
        if (!AppearancePresets.IdPattern.IsMatch(id) || kind is not (KindToggle or KindChoice or KindNumber or KindColour)) return null;
        var choices = (item["choices"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(choice => new AppearanceChoice(Text(choice, "value"), Text(choice, "label"))).Where(choice => choice.Value.Length > 0).ToList();
        if (kind == KindChoice && choices.Count == 0) return null;
        return new(id, Text(item, "category"), kind, Text(item, "label"), choices, Number(item, "min"), Number(item, "max"), Math.Max(1, Number(item, "step")),
            Text(item, "apply"), Text(item, "risk"), Text(item, "value"), item["changed"] is JsonValue changed && changed.TryGetValue<bool>(out var flag) && flag);
    }

    static string Text(JsonObject item, string key) => item[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    static int Number(JsonObject item, string key) => item[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
}

/// <summary>One line of the Appearance window: a setting, how many of the selected computers have it, and what they show now.</summary>
public sealed record AppearanceRow(AppearanceSettingInfo Setting, int SupportedBy, int Total, IReadOnlyList<string> CurrentValues, int ChangedOn)
{
    public bool Mixed => CurrentValues.Count > 1;
}

/// <summary>Puts the answers of several computers together into the rows the window draws.</summary>
public static class AppearanceMerge
{
    static readonly string[] CategoryOrder = ["Colours", "Desktop", "Taskbar", "Start", "File Explorer", "Cursor and text", "Effects", "Sound", "Notifications", "Display", "Lock and sign-in"];

    /// <summary>One answer per selected computer; null for a computer that did not answer. A setting is drawn once, from the first computer that has it.</summary>
    public static IReadOnlyList<AppearanceRow> Rows(IReadOnlyList<AppearanceComputerCatalogue?> computers)
    {
        var total = computers.Count;
        return computers.Where(computer => computer is not null).SelectMany(computer => computer!.Settings).GroupBy(setting => setting.Id)
            .Select(group => new AppearanceRow(group.First(), group.Count(), total,
                group.Select(setting => setting.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), group.Count(setting => setting.Changed)))
            .OrderBy(row => Rank(row.Setting.Category)).ThenBy(row => row.Setting.Category, StringComparer.Ordinal)
            .ThenBy(row => row.Setting.Id, StringComparer.Ordinal).ToArray();
    }

    static int Rank(string category) { var index = Array.IndexOf(CategoryOrder, category); return index < 0 ? CategoryOrder.Length : index; }
}

/// <summary>A saved set of setting values the teacher can apply to a room. Values are the Host's words, as <c>appearance.set</c> takes them.</summary>
public sealed record AppearancePreset(string Name, IReadOnlyDictionary<string, string> Values);

/// <summary>The rules for a preset, and the check of a preset against what the selected computers offer.</summary>
public static class AppearancePresets
{
    public static readonly Regex IdPattern = new(@"^[a-z]+(\.[a-z][a-z0-9-]*)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public const int MaxNameLength = 60;
    public const int MaxValues = 64;
    public const int MaxValueLength = 100;

    /// <summary>Why a preset is not acceptable as saved or imported. An empty list means it is.</summary>
    public static IReadOnlyList<string> Problems(AppearancePreset preset)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > MaxNameLength) problems.Add($"The name must be 1 to {MaxNameLength} characters.");
        if (preset.Values.Count == 0) problems.Add("The preset sets nothing.");
        if (preset.Values.Count > MaxValues) problems.Add($"A preset sets at most {MaxValues} settings.");
        foreach (var (id, value) in preset.Values)
        {
            if (!IdPattern.IsMatch(id)) problems.Add($"\"{id}\" is not a setting name.");
            if (value is null || value.Length > MaxValueLength) problems.Add($"The value of \"{id}\" is missing or too long.");
        }
        return problems;
    }

    /// <summary>
    /// Why a preset cannot be applied to the computers behind <paramref name="rows"/>: a setting none of them has, or a value the setting
    /// cannot take. A preset that fails here is refused whole, so nothing is half applied. A setting some computers lack is not a problem:
    /// those computers report it as not supported.
    /// </summary>
    public static IReadOnlyList<string> Plan(AppearancePreset preset, IReadOnlyList<AppearanceRow> rows)
    {
        var problems = new List<string>(Problems(preset));
        var byId = rows.ToDictionary(row => row.Setting.Id, StringComparer.Ordinal);
        foreach (var (id, value) in preset.Values)
        {
            if (!IdPattern.IsMatch(id)) continue;
            if (!byId.TryGetValue(id, out var row)) { problems.Add($"None of the selected computers has the setting \"{id}\"."); continue; }
            if (!ValueFits(row.Setting, value)) problems.Add($"\"{value}\" is not a value of \"{row.Setting.Label}\".");
        }
        return problems;
    }

    public static bool ValueFits(AppearanceSettingInfo setting, string value) => setting.Kind switch
    {
        AppearanceWireReader.KindToggle => value is "true" or "false",
        AppearanceWireReader.KindChoice => setting.Choices.Any(choice => choice.Value == value),
        AppearanceWireReader.KindNumber => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
            number >= setting.Min && number <= setting.Max && (number - setting.Min) % Math.Max(1, setting.Step) == 0,
        AppearanceWireReader.KindColour => Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$"),
        _ => false,
    };

    /// <summary>The preset as JSON text for saving or exporting.</summary>
    public static string ToJson(AppearancePreset preset) => new JsonObject
    {
        ["name"] = preset.Name,
        ["values"] = new JsonObject(preset.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
    }.ToJsonString();

    /// <summary>Reads a preset from JSON text; null when it is not a preset. Judge it with <see cref="Problems"/>.</summary>
    public static AppearancePreset? FromJson(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["values"] is not JsonObject values) return null;
            var name = root["name"] is JsonValue text && text.TryGetValue<string>(out var value) ? value : "";
            var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, node) in values)
            {
                if (node is not JsonValue entry || !entry.TryGetValue<string>(out var setting)) return null;
                pairs[id] = setting;
            }
            return new AppearancePreset(name, pairs);
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
