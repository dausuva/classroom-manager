// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Modules;

/// <summary>
/// One file of a transfer could not be copied (the computer could not read it, or this PC could not write it) but the run can go on with
/// the next one. A lost connection is not this: it ends the run and passes through as the exception it is.
/// </summary>
public sealed class ModuleTransferException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The one rule for a relative path a module names: used by the Host to refuse a bad destination and by modules to avoid sending one.</summary>
public static class ModulePaths
{
    static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>
    /// Why a relative path is unsafe, or null. Either slash is accepted. No drive or stream, no rooted or empty, "." or ".." part, no part that
    /// ends with a space or a dot, no character Windows does not allow in a name, no device name, and at most 200 characters.
    /// </summary>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "the path is empty.";
        if (path.Length > 200) return "the path is longer than 200 characters.";
        if (path.Contains(':')) return "use no drive or stream names.";
        var forward = path.Replace('\\', '/');
        if (forward.StartsWith('/')) return "the path must be relative.";
        foreach (var part in forward.Split('/'))
        {
            if (part.Length == 0 || part == "." || part == "..") return "the path must not contain empty, \".\" or \"..\" parts.";
            if (part != part.TrimEnd(' ', '.')) return "a part of the path ends with a space or a dot.";
            if (part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return "the path contains a character Windows does not allow.";
            var stem = part.Contains('.') ? part[..part.IndexOf('.')] : part;
            if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return "the path uses a reserved Windows device name.";
        }
        return null;
    }
}
