// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>Limits the Host dialog and the client handler both apply to screen saver changes.</summary>
public static class ScreenSaverRules
{
    public const string Extension = ".scr";

    /// <summary>Windows' own dialog offers 1 to 9999 minutes.</summary>
    public const int MinTimeoutSeconds = 60;
    public const int MaxTimeoutSeconds = 9999 * 60;

    /// <summary>A screen saver is a small program; anything larger is almost certainly the wrong file.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    public static int ClampTimeoutSeconds(int seconds) =>
        Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds);

    /// <summary>
    /// Returns <paramref name="name"/> when it is a plain ".scr" file name that is safe to create in the
    /// managed folder, otherwise an empty string. Paths, device names, and other extensions are rejected.
    /// </summary>
    public static string SafeFileName(string? name)
    {
        var value = (name ?? "").Trim();
        if (value.Length is 0 or > 80) return "";
        if (!value.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return "";

        var stem = value[..^Extension.Length];
        if (stem.Length == 0 || stem.StartsWith('.') || stem.EndsWith('.') || stem.EndsWith(' ')) return "";
        if (!stem.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')')) return "";
        return ReservedDeviceNames.Contains(stem.Split('.')[0]) ? "" : value;
    }

    static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
}
