// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;

namespace ClassroomManager.Host;

public static class StagingNamespace
{
    public const string StagingRoot = @"C:\ProgramData\ClassroomManager\Staging";

    public static string Id(HostSettings settings) => $"{settings.HostId}-{settings.UploadToken}";

    public static string Root(HostSettings settings) => Path.Combine(StagingRoot, "Hosts", Id(settings));

    public static string RemotePath(HostSettings settings, string relativePath)
    {
        var relative = NormalizeRelativePath(relativePath);
        return Path.Combine(Root(settings), relative);
    }

    public static string NormalizeRelativePath(string value)
    {
        value = (value ?? "").Replace('/', Path.DirectorySeparatorChar).Trim().TrimStart(Path.DirectorySeparatorChar);
        if (value.Length == 0 || Path.IsPathRooted(value) ||
            value.Split('\\', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw new ArgumentException("A staging path must be a non-empty relative path without '.' or '..'.", nameof(value));
        return value;
    }
}
