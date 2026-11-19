// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Reflection;

namespace ClassroomManager.Host;

/// <summary>
/// Whether this Host is an official build (the Microsoft Store package and the signed builds Dausuva MB makes) or a free build made from the source.
/// The release scripts set the MSBuild property ClassroomOfficial; a build made from the public source does not, and has no computer limit.
/// </summary>
public static class BuildEdition
{
    public static bool IsOfficial { get; } = Read(typeof(BuildEdition).Assembly);

    /// <summary>
    /// Whether this Host applies the free tier's limit of 3 computers online at once (a licence file lifts it): the official builds, and the unsigned release builds
    /// the public repository's release workflow makes (they pass ClassroomFreeLimit). A build you make yourself from the source has no limit.
    /// </summary>
    public static bool HasFreeLimit { get; } = ReadFreeLimit(typeof(BuildEdition).Assembly);

    internal static bool ReadFreeLimit(Assembly assembly) =>
        Read(assembly) || assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "ClassroomFreeLimit" && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase));

    internal static bool Read(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "ClassroomOfficial" && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase));
}
