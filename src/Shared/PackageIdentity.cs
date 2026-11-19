// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.InteropServices;
using System.Text;

namespace ClassroomManager.Shared;

/// <summary>
/// Whether this process runs inside an MSIX package (the Microsoft Store build, or a sideloaded package), and under which package name. A packaged program
/// is installed, updated and removed by Windows, not by this program: it must not offer to install itself or to replace its own files, and its
/// per-user files may be kept in the package's own store (see docs/windows/STORE-AND-WINGET.md).
/// </summary>
public static class PackageIdentity
{
    const int AppModelErrorNoPackage = 15700;
    const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    static readonly Lazy<string?> Name = new(Read);

    /// <summary>True when Windows has given this process a package identity.</summary>
    public static bool IsPackaged => FullName is not null;

    /// <summary>The package full name (for example <c>Dausuva.ClassroomHost_1.2.0.0_x64__8wekyb3d8bbwe</c>), or null when the program is not packaged.</summary>
    public static string? FullName => Name.Value;

    static string? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            if (result == AppModelErrorNoPackage) return null;
            if (result != ErrorInsufficientBuffer || length <= 0) return null;
            var name = new StringBuilder(length);
            return GetCurrentPackageFullName(ref length, name) == 0 ? name.ToString() : null;
        }
        catch (EntryPointNotFoundException) { return null; }   // Windows before 8
        catch (DllNotFoundException) { return null; }
    }

    /// <summary>The package name part of a full name ("Dausuva.ClassroomHost" of "Dausuva.ClassroomHost_1.2.0.0_x64__8wekyb3d8bbwe"), for a log line.</summary>
    public static string ShortName(string fullName) => fullName.Split('_')[0];
}
