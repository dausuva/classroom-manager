// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.Cryptography;
using System.Text;

namespace ClassroomManager.Shared;

/// <summary>
/// The id of the Windows installation a Host runs on, which a licence can be tied to (T-262). It is the same for every Windows user of the
/// PC and does not change when the Host is updated or reinstalled; a new Windows installation is a new computer. It is a hash, so it says
/// nothing about the PC, and it is written as 20 hexadecimal digits in groups of four (<c>A1B2-C3D4-E5F6-0718-293A</c>) so it can be read
/// aloud, e-mailed and typed into Classroom Desk.
/// </summary>
public static class ComputerId
{
    public const int Digits = 20;

    /// <summary>The id for the machine identity Windows keeps for the installation (the MachineGuid value).</summary>
    public static string FromMachineGuid(string machineGuid)
    {
        if (string.IsNullOrWhiteSpace(machineGuid)) throw new ArgumentException("The computer has no machine identity.", nameof(machineGuid));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("classroom-manager-computer-v1|" + machineGuid.Trim().ToLowerInvariant()));
        return Format(Convert.ToHexString(hash)[..Digits]);
    }

    /// <summary>The id in its fixed written form: upper case, groups of four separated by hyphens. Null when the text is not an id.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var digits = new StringBuilder();
        foreach (var c in text)
        {
            if (c is '-' or ' ' or '_') continue;
            if (!Uri.IsHexDigit(c)) return null;
            digits.Append(char.ToUpperInvariant(c));
        }
        return digits.Length == Digits ? Format(digits.ToString()) : null;
    }

    static string Format(string digits) => string.Join('-', Enumerable.Range(0, Digits / 4).Select(group => digits.Substring(group * 4, 4)));

    public static bool Same(string? left, string? right) =>
        Normalize(left) is { } a && Normalize(right) is { } b && string.Equals(a, b, StringComparison.Ordinal);
}
