// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>
/// What Windows accepts as a computer name (<see cref="Cmd.SystemRename"/>). Checked by the Host before it asks
/// and again by the Client before it changes anything. The protocol version from which a Client can rename is
/// <see cref="RequiredVersion"/>.
/// </summary>
public static class ComputerNameRules
{
    public const string RequiredVersion = "1.16";

    /// <summary>The length of a NetBIOS name, which a Windows computer name must fit.</summary>
    public const int MaxLength = 15;

    public const string Name = "name";
    public const string OldName = "oldName";
    public const string NewName = "newName";
    public const string RestartRequired = "restartRequired";

    /// <summary>Why <paramref name="name"/> cannot be a computer name, or null when it can.</summary>
    public static string? Problem(string? name)
    {
        var text = name ?? "";
        if (text.Length == 0) return "Enter a computer name.";
        if (text.Length > MaxLength) return $"A computer name has at most {MaxLength} characters.";
        if (text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            return "Use only the letters A-Z, digits and hyphens (no spaces or other characters).";
        if (text.StartsWith('-') || text.EndsWith('-')) return "A computer name cannot start or end with a hyphen.";
        if (text.All(char.IsAsciiDigit)) return "A computer name cannot be only digits.";
        return null;
    }

    /// <summary>True when the two names are the same computer name (Windows ignores the case).</summary>
    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
