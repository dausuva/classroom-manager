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

    internal static bool Read(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "ClassroomOfficial" && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase));
}
