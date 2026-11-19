// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.RegularExpressions;

namespace ClassroomManager.Shared;

public sealed record SilentUninstallInfo(bool IsSilent, string Source, string SuggestedArgs, string Note);

public static class UninstallGuidance
{
    static readonly Regex InnoUninstaller = new(@"^unins\d*\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static SilentUninstallInfo Analyze(bool isMsi, string? quietUninstallString, string? uninstallString)
    {
        if (isMsi)
            return new(true, "Windows Installer (/qn /norestart)", "", "The Client builds the uninstall command from the MSI product code.");

        if (!string.IsNullOrWhiteSpace(quietUninstallString))
            return new(true, "Registry quiet command", "", "The application's uninstall registry entry explicitly provides a quiet command.");

        var executable = ExecutableFromCommand(uninstallString ?? "");
        if (InnoUninstaller.IsMatch(Path.GetFileName(executable)))
            return new(false, "Inno Setup candidate", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                "The uninstaller name matches Inno Setup. Review and test these documented Inno switches on one computer first.");

        return new(false, "Not advertised", "",
            "No trusted silent command was found. Check the publisher's deployment documentation or test a package-manager uninstall on one computer.");
    }

    static string ExecutableFromCommand(string command)
    {
        var value = Environment.ExpandEnvironmentVariables(command).Trim();
        if (value.StartsWith('"'))
        {
            var closing = value.IndexOf('"', 1);
            return closing > 1 ? value[1..closing] : value.Trim('"');
        }

        var firstSpace = value.IndexOf(' ');
        return firstSpace < 0 ? value : value[..firstSpace];
    }
}
