// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Appearance;

namespace ClassroomManager.Host;

/// <summary>A preset in a file, for moving it to another Host or keeping it: a small JSON file, read with the same rules as a preset the teacher saves.</summary>
public static class AppearancePresetFiles
{
    /// <summary>A preset is a few lines of text; a bigger file is not one.</summary>
    public const long MaxBytes = 64 * 1024;
    public const string Extension = ".appearance-preset.json";

    /// <summary>Writes the preset to <paramref name="path"/>. It is checked first, so a file this writes is always one <see cref="Read"/> accepts.</summary>
    public static void Write(AppearancePreset preset, string path)
    {
        var problems = AppearancePresets.Problems(preset);
        if (problems.Count > 0) throw new InvalidDataException(string.Join(" ", problems));
        File.WriteAllText(path, AppearancePresets.ToJson(preset) + Environment.NewLine, new System.Text.UTF8Encoding(false));
    }

    /// <summary>Reads a preset file. Nothing is taken from a file that is too big, is not a preset, or that <see cref="AppearancePresets.Problems"/> refuses.</summary>
    public static (AppearancePreset? Preset, IReadOnlyList<string> Problems) Read(string path)
    {
        FileInfo info;
        try { info = new FileInfo(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return (null, ["The file name is not valid."]); }
        if (!info.Exists) return (null, ["The file was not found."]);
        if (info.Length > MaxBytes) return (null, ["The file is too big to be a preset."]);
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, ["The file could not be read."]); }
        var preset = AppearancePresets.FromJson(text);
        if (preset is null) return (null, ["The file is not an Appearance preset."]);
        var problems = AppearancePresets.Problems(preset);
        return (problems.Count == 0 ? preset : null, problems);
    }
}
