// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Shared;
using Microsoft.Win32;

namespace ClassroomManager.Host;

/// <summary>
/// The id of this computer for licences (T-262): derived from the machine identity Windows keeps for the installation, which every Windows user
/// of the PC reads the same. See <see cref="ComputerId"/>.
/// </summary>
public static class HostComputerId
{
    static string? _current;

    /// <summary>Replaced by tests: the machine identity to use instead of the one in the registry.</summary>
    internal static Func<string?> ReadMachineGuid { get; set; } = FromRegistry;

    /// <summary>The id of this computer, or "" when Windows gives no machine identity (a bound licence then cannot match).</summary>
    public static string Current
    {
        get
        {
            if (_current is not null) return _current;
            var guid = ReadMachineGuid();
            return _current = string.IsNullOrWhiteSpace(guid) ? "" : ComputerId.FromMachineGuid(guid);
        }
    }

    internal static void Reset() => _current = null;

    static string? FromRegistry()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
