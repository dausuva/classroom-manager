// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;

namespace ClassroomManager.Shared;

/// <summary>
/// The Windows version of a computer ("24H2") and the version it is told to move to.
///
/// A new Windows version is offered in Settings with "Download and install" and waits there until
/// someone clicks it; an update check does not list it. Setting a target version (the Windows
/// Update for Business "target release version" policy) turns it into an ordinary update, which
/// then shows up in the check and installs with the others. The policy also keeps the computer on
/// that version afterwards, until the target is changed or removed.
/// </summary>
public static class WindowsRelease
{
    // Fields of windows.update.status (absent on clients older than this feature).
    public const string Product = "product";               // "Windows 10" or "Windows 11"
    public const string Version = "displayVersion";        // "24H2"
    public const string Build = "build";                   // "26100.2033"
    public const string Edition = "edition";               // "Professional", "Education", "Core" (Home), ...
    public const string TargetVersion = "targetVersion";   // "" when no target is set
    public const string RestartPending = "restartPending";

    /// <summary>The "Upgrades" classification Windows Update gives a new Windows version.</summary>
    public const string UpgradesCategory = "3689bdc8-b205-4af4-8d4a-a63924c5e9d5";

    /// <summary>A version name such as "25H2", in upper case; empty when the text is not one.</summary>
    public static string Normalize(string? text)
    {
        var value = (text ?? "").Trim().ToUpperInvariant();
        return value.Length == 4 && char.IsAsciiDigit(value[0]) && char.IsAsciiDigit(value[1]) && value[2] == 'H' && value[3] is '1' or '2' ? value : "";
    }

    /// <summary>Windows 11 began at build 22000; the registry still calls it "Windows 10", so the build decides.</summary>
    public static string ProductOfBuild(int build) => build >= 22000 ? "Windows 11" : "Windows 10";

    /// <summary>Negative when <paramref name="version"/> is older than <paramref name="other"/>; 0 when equal or when either is not a version name.</summary>
    public static int Compare(string? version, string? other)
    {
        string a = Normalize(version), b = Normalize(other);
        return a.Length == 0 || b.Length == 0 ? 0 : string.CompareOrdinal(a, b);
    }

    /// <summary>Home editions ignore the target version policy.</summary>
    public static bool EditionTakesTarget(string? edition) =>
        !(edition ?? "").StartsWith("Core", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for an update that installs a new Windows version rather than patching the current one.</summary>
    public static bool IsFeatureUpdate(string? title, IEnumerable<string> categoryIds)
    {
        if (categoryIds.Any(id => id.Equals(UpgradesCategory, StringComparison.OrdinalIgnoreCase))) return true;
        var text = (title ?? "").Trim();
        return text.StartsWith("Feature update to Windows", StringComparison.OrdinalIgnoreCase) ||
               ((text.StartsWith("Windows 11, version", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Windows 10, version", StringComparison.OrdinalIgnoreCase)) &&
                !text.Contains("KB", StringComparison.Ordinal));
    }

    /// <summary>"Windows 11 24H2 (build 26100.2033)", or empty when the client did not report a version.</summary>
    public static string Describe(JsonObject status)
    {
        string product = Text(status, Product), version = Text(status, Version), build = Text(status, Build);
        if (product.Length == 0 && version.Length == 0) return "";
        return $"{product} {version}".Trim() + (build.Length > 0 ? $" (build {build})" : "");
    }

    /// <summary>What a status says about the target version and a waiting restart; empty when there is nothing to say.</summary>
    public static string DescribeTarget(JsonObject status)
    {
        var parts = new List<string>();
        string target = Text(status, TargetVersion), version = Text(status, Version);
        if (target.Length > 0)
            parts.Add(Compare(version, target) < 0 ? $"moving to {target}" : $"kept on {target}");
        if (status[RestartPending]?.GetValue<bool>() == true) parts.Add("restart pending");
        return string.Join(" · ", parts);
    }

    static string Text(JsonObject data, string key) => data[key]?.GetValue<string>() ?? "";
}
