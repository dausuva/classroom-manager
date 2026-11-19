// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Appearance;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>What <c>ClassroomCli appearance</c> does: lists, exports and imports the Appearance presets saved in this Host, and applies one to computers.</summary>
public static class CliAppearance
{
    public static List<CliResult> List(HostSettings settings) =>
        settings.AppearancePresets.OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .Select(preset => new CliResult(preset.Name, "", "", true, $"{preset.Values.Count} setting(s)")).ToList();

    public static AppearancePreset Find(HostSettings settings, string name)
    {
        var matches = settings.AppearancePresets.Where(preset => preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => new AppearancePreset(matches[0].Name, matches[0].Values),
            0 => throw new CliUsageException($"There is no Appearance preset named '{name}'. Run 'ClassroomCli appearance presets' to list them."),
            _ => throw new CliUsageException($"{matches.Count} Appearance presets are named '{name}'; rename them in the Host so the name is unique."),
        };
    }

    /// <summary>Writes one saved preset to a file. Reads the settings only.</summary>
    public static string Export(HostSettings settings, string name, string file)
    {
        var preset = Find(settings, name);
        try { AppearancePresetFiles.Write(preset, file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { throw new CliUsageException($"The preset could not be written: {ex.Message}"); }
        return $"Preset \"{preset.Name}\" ({preset.Values.Count} setting(s)) written to {file}";
    }

    /// <summary>Adds the preset in a file to the settings (a preset of the same name is replaced) but does not save them; the caller does.</summary>
    public static string Import(HostSettings settings, string file)
    {
        var (preset, problems) = AppearancePresetFiles.Read(file);
        if (preset is null) throw new CliUsageException($"{file}: {string.Join(" ", problems)}");
        settings.AppearancePresets.RemoveAll(existing => existing.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
        settings.AppearancePresets.Add(new AppearancePresetRecord { Name = preset.Name, Values = new Dictionary<string, string>(preset.Values, StringComparer.Ordinal) });
        return $"Preset \"{preset.Name}\" ({preset.Values.Count} setting(s)) imported from {file}";
    }

    /// <summary>Applies a preset to one computer through the Appearance module's ports, refused whole when the computer lacks a setting in it. Throws when anything failed.</summary>
    public static async Task<string> ApplyAsync(ClientInfo client, ClientSession session, HostSettings settings, string name, CancellationToken ct)
    {
        var preset = Find(settings, name);
        var (appearance, machine) = ModuleRuns.OpenAppearance(session, client);
        var rows = AppearanceMerge.Rows([await appearance.CatalogAsync(machine, ct)]);
        var problems = AppearancePresets.Plan(preset, rows);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(" ", problems));
        return Summary(await appearance.SetAsync(machine, preset.Values, ct));
    }

    public static string Summary(IReadOnlyList<AppearanceResult> results)
    {
        int Count(string status) => results.Count(result => result.Status == status);
        var parts = new List<string>();
        if (Count(AppearanceWire.Applied) > 0) parts.Add($"{Count(AppearanceWire.Applied)} applied");
        if (Count(AppearanceWire.Unchanged) > 0) parts.Add($"{Count(AppearanceWire.Unchanged)} already that way");
        if (Count(AppearanceWire.Unsupported) > 0) parts.Add($"{Count(AppearanceWire.Unsupported)} not supported on this Windows");
        if (results.FirstOrDefault(result => result.ConfirmSeconds > 0) is { } waiting)
            parts.Add($"waiting for the person at the computer to keep the change ({waiting.ConfirmSeconds} seconds)");
        var failed = results.Where(result => result.Status == AppearanceWire.Failed).ToList();
        if (failed.Count > 0) throw new InvalidOperationException(string.Join("; ", failed.Select(item => $"{item.Id}: {item.Reason}")) + (parts.Count > 0 ? " (" + string.Join("; ", parts) + ")" : ""));
        return parts.Count == 0 ? "nothing to change" : string.Join("; ", parts);
    }
}
