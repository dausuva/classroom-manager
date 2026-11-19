// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>How a program's version is shown to a person (T-218).</summary>
public static class ReleaseVersions
{
    /// <summary>
    /// A customer release has the Windows file version <c>1.2.0.0</c>; the customer is shown <c>1.2.0</c>. An internal build always has a build
    /// number in the fourth place, so it is shown as it is. Anything that is not a four-part version is returned unchanged.
    /// </summary>
    public static string Display(string? fileVersion)
    {
        if (string.IsNullOrWhiteSpace(fileVersion)) return fileVersion ?? "";
        var parts = fileVersion.Split('.');
        return parts.Length == 4 && parts[3] == "0" && Version.TryParse(fileVersion, out _) ? string.Join('.', parts[..3]) : fileVersion;
    }
}
