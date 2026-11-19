// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>scripts/Test-AntivirusPrecheck.ps1 (T-240): reading what Defender says, refusing nonsense, and, where Defender is on, a real scan of a harmless file. No test writes the EICAR string: a scan that reports a virus is tried by hand (docs/release/AUTOMATIC-RELEASE-GUIDE.md).</summary>
public sealed class AntivirusPrecheckTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "classroom-av-" + Guid.NewGuid().ToString("N"));

    public AntivirusPrecheckTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(PowerShellEdition.Windows)]
    [InlineData(PowerShellEdition.Core)]
    public async Task The_scanner_output_is_read_as_clean_a_threat_or_an_unfinished_scan(PowerShellEdition edition)
    {
        var probe = Path.Combine(_folder, "read-output.ps1");
        var module = Path.Combine(StringExtractor.RepositoryRoot(), "scripts", "AntivirusPrecheck.psm1");
        File.WriteAllText(probe, $$"""
            Import-Module '{{module}}' -Force
            $clean = ConvertFrom-MpCmdRunOutput -Text "Scan starting...`nScan finished.`nScanning C:\x\a.exe found no threats." -ExitCode 0
            $threat = ConvertFrom-MpCmdRunOutput -Text "Scan finished.`nScanning C:\x\a.exe found 1 threats.`nThreat                  : Trojan:Win32/Example.A!ml`n" -ExitCode 2
            $broken = ConvertFrom-MpCmdRunOutput -Text "Scan starting..." -ExitCode 2
            $lyingExit = ConvertFrom-MpCmdRunOutput -Text "Scan finished.`nScanning C:\x\a.exe found no threats." -ExitCode 2
            [pscustomobject]@{
                cleanIsClean = $clean.Clean; threatIsClean = $threat.Clean; threatName = ($threat.Threats -join ','); threatCompleted = $threat.Completed
                brokenIsClean = $broken.Clean; brokenCompleted = $broken.Completed; lyingExitIsClean = $lyingExit.Clean
                eicarLength = (Get-EicarTestText).Length; age = (Get-SignatureAgeDays -LastUpdated ([datetime]'2026-11-01') -Now ([datetime]'2026-11-03 12:00'))
            } | ConvertTo-Json
            """);
        var (exit, text, errors) = await PowerShellScript.RunAsync(edition, probe, [], TimeSpan.FromMinutes(1));
        Assert.True(exit == 0, text + errors);
        using var json = JsonDocument.Parse(text);
        var result = json.RootElement;
        Assert.True(result.GetProperty("cleanIsClean").GetBoolean());
        Assert.False(result.GetProperty("threatIsClean").GetBoolean());
        Assert.Equal("Trojan:Win32/Example.A!ml", result.GetProperty("threatName").GetString());
        Assert.True(result.GetProperty("threatCompleted").GetBoolean());
        Assert.False(result.GetProperty("brokenIsClean").GetBoolean());
        Assert.False(result.GetProperty("brokenCompleted").GetBoolean());
        Assert.False(result.GetProperty("lyingExitIsClean").GetBoolean(), "A non-zero exit code is never a clean scan, whatever the text says.");
        Assert.Equal(68, result.GetProperty("eicarLength").GetInt32());
        Assert.Equal(2.5, result.GetProperty("age").GetDouble());
    }

    [Fact]
    public async Task A_missing_path_and_a_report_folder_inside_the_scanned_folder_are_refused()
    {
        var (missingExit, missingText, missingErrors) = await PowerShellScript.RunAsync(PowerShellEdition.Core, Path.Combine("scripts", "Test-AntivirusPrecheck.ps1"),
            ["-Path", Path.Combine(_folder, "nothing-here"), "-ReportFolder", Path.Combine(_folder, "report")], TimeSpan.FromMinutes(1));
        Assert.NotEqual(0, missingExit);
        Assert.Contains("Not found", missingText + missingErrors);

        File.WriteAllText(Path.Combine(_folder, "a.txt"), "harmless");
        var (insideExit, insideText, insideErrors) = await PowerShellScript.RunAsync(PowerShellEdition.Core, Path.Combine("scripts", "Test-AntivirusPrecheck.ps1"),
            ["-Path", _folder, "-ReportFolder", Path.Combine(_folder, "report")], TimeSpan.FromMinutes(1));
        Assert.NotEqual(0, insideExit);
        Assert.Contains("must not be inside", insideText + insideErrors);
    }

    [Theory]
    [InlineData(PowerShellEdition.Windows)]
    [InlineData(PowerShellEdition.Core)]
    public async Task A_harmless_file_is_scanned_and_reported_clean_or_the_check_says_it_is_incomplete(PowerShellEdition edition)
    {
        var files = Path.Combine(_folder, "release");
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(files, "notes.txt"), "A harmless text file.");
        var reports = Path.Combine(_folder, "reports");

        var (exit, text, errors) = await PowerShellScript.RunAsync(edition, Path.Combine("scripts", "Test-AntivirusPrecheck.ps1"),
            ["-Path", files, "-ReportFolder", reports, "-MaxSignatureAgeDays", "3650"], TimeSpan.FromMinutes(3));

        // Exit 0 = scanned clean. Exit 2 = Defender is not the active antivirus on this machine (a CI runner may have it off): the script says so and does not call that clean.
        Assert.True(exit is 0 or 2, text + errors);
        var report = File.ReadAllText(Assert.Single(Directory.GetFiles(reports, "*.md")));
        Assert.Contains(exit == 0 ? "CLEAN" : "INCOMPLETE", report);
        Assert.Contains("notes.txt", report);
        Assert.Contains("Nothing was uploaded", report);
        Assert.DoesNotContain("av-selftest", Directory.GetFiles(files).Select(Path.GetFileName).FirstOrDefault(name => name != "notes.txt") ?? "");
    }
}
