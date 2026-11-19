// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>Rules for the label a teacher gives a computer in the Host.</summary>
public static class ComputerLabel
{
    public const int MaxLength = 40;

    /// <summary>The label as kept: one line, no stray spaces, not too long.</summary>
    public static string Clean(string? text)
    {
        var line = string.Join(" ", (text ?? "").Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > MaxLength ? line[..MaxLength].TrimEnd() : line;
    }
}
