// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>
/// A path as a person types or pastes it. Explorer's "Copy as path" puts the path in quotes, which a
/// teacher then pastes into a folder field; Windows does not allow quotes in a name, so the quoted text
/// names no folder at all, and with a space in the path that looks like "paths with spaces do not work".
/// </summary>
public static class PathText
{
    /// <summary>The path without the spaces around it and without one pair of surrounding quotes.</summary>
    public static string Clean(string? path)
    {
        var text = (path ?? "").Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1].Trim();
        else if (text.Length >= 1 && text[0] == '"' && text.IndexOf('"', 1) < 0) text = text[1..].Trim();
        return text;
    }
}
