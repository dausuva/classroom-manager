// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>scripts/winget/New-WingetManifests.ps1 writes manifests that winget accepts, for the Host, the Client and the portable Host.</summary>
public sealed class WingetManifestTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "classroom-winget-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    const string Hash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Theory]
    [InlineData(PowerShellEdition.Windows, "Host", "Dausuva.ClassroomHost", "ClassroomHost", "inno")]
    [InlineData(PowerShellEdition.Windows, "Client", "Dausuva.ClassroomClient", "ClassroomClient", "inno")]
    [InlineData(PowerShellEdition.Core, "HostPortable", "Dausuva.ClassroomHost.Portable", "ClassroomHost.Portable", "portable")]
    [InlineData(PowerShellEdition.Core, "Host", "Dausuva.ClassroomHost", "ClassroomHost", "inno")]
    public async Task The_manifests_have_the_three_files_the_right_installer_type_and_pass_winget_validate(
        PowerShellEdition edition, string component, string id, string folder, string installerType)
    {
        var (exit, output, errors) = await PowerShellScript.RunAsync(edition, Path.Combine("scripts", "winget", "New-WingetManifests.ps1"),
            ["-Component", component, "-Version", "1.2.0", "-InstallerUrl", $"https://github.com/dausuva/classroom-manager/releases/download/v1.2.0/{component}.exe",
             "-InstallerSha256", Hash.ToLowerInvariant(), "-OutputRoot", _folder, "-ReleaseDate", "2026-11-03"], TimeSpan.FromMinutes(2));
        Assert.True(exit == 0, output + errors);

        var directory = Path.Combine(_folder, "manifests", "d", "Dausuva", folder, "1.2.0");
        Assert.Equal([$"{id}.installer.yaml", $"{id}.locale.en-US.yaml", $"{id}.yaml"], Directory.GetFiles(directory).Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal).ToArray());

        var installer = File.ReadAllText(Path.Combine(directory, $"{id}.installer.yaml"));
        Assert.Contains($"InstallerType: {installerType}", installer);
        Assert.Contains("InstallerSha256: " + Hash, installer);      // upper case, whatever case it was given in
        Assert.Contains($"PackageIdentifier: {id}", installer);
        Assert.Equal(installerType == "portable", installer.Contains("Commands:"));

        // The public listing never points at the private repository.
        foreach (var file in Directory.GetFiles(directory))
            Assert.DoesNotContain("edtechlabs", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);

        if (Validate(directory) is { } result) Assert.True(result.Exit == 0 || OnlySchemaHeaderWarnings(result.Text), result.Text);
    }

    /// <summary>
    /// An older winget (the one on a CI runner) does not know the newest schema version and warns that the "# yaml-language-server" header does not match its pattern; that comment is
    /// only for editors. Any other warning or error still fails the test.
    /// </summary>
    static bool OnlySchemaHeaderWarnings(string text)
    {
        if (!text.Contains("Manifest validation succeeded with warnings", StringComparison.OrdinalIgnoreCase)) return false;
        var warnings = text.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("Manifest Warning:", StringComparison.OrdinalIgnoreCase)).ToArray();
        return warnings.Length > 0 && warnings.All(line => line.Contains("schema header URL", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs `winget validate` when winget is installed (it is on Windows 10 1809 and later with App Installer); null when it is not.</summary>
    static (int Exit, string Text)? Validate(string directory)
    {
        try
        {
            var start = new ProcessStartInfo("winget") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("validate");
            start.ArgumentList.Add(directory);
            using var process = Process.Start(start)!;
            var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(120_000)) { process.Kill(true); return (1, "winget validate timed out"); }
            return (process.ExitCode, text);
        }
        catch (Win32Exception) { return null; }
    }
}
