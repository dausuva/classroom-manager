// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// deploy/ClassroomSetup.psm1 (T-218): the installer must replace a customer release with a later one of the same public version, although both have
/// the Windows file version 1.2.0.0. Setup runs on school PCs, so this is tested under Windows PowerShell 5.1 only.
/// </summary>
public sealed class SetupReleaseOrderTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "cm-setup-order-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
        catch (IOException) { }
    }

    static string Claim(string version = "1.2.0", string audience = "school-a", string variant = "windows", int revision = 1) =>
        $"cm-release/1|{version}|customer|{audience}|{variant}|{revision}|rel-{revision}";

    /// <summary>One line per case: "name=problem" ('' when the update may go on), plus "name:handled".</summary>
    async Task<Dictionary<string, string>> EvaluateAsync(params (string Name, string? Installed, string InstalledVersion, string? New, string NewVersion, bool Force)[] cases)
    {
        Directory.CreateDirectory(_folder);
        var script = Path.Combine(_folder, "cases.ps1");
        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            $"Import-Module '{Path.Combine(StringExtractor.RepositoryRoot(), "deploy", "ClassroomSetup.psm1")}' -Force",
        };
        foreach (var (name, installed, installedVersion, candidate, newVersion, force) in cases)
            lines.Add($"$r = Test-CmReleaseOrder -Installed (ConvertFrom-CmReleaseClaim {Quote(installed)}) -InstalledVersion ([version]'{installedVersion}') -New (ConvertFrom-CmReleaseClaim {Quote(candidate)}) -NewVersion ([version]'{newVersion}'){(force ? " -Force" : "")}; " +
                      $"Write-Output ('{name}=' + $r.Problem + '|' + $r.Handled)");
        File.WriteAllLines(script, lines);
        var (exit, output, errors) = await PowerShellScript.RunAsync(PowerShellEdition.Windows, Path.GetRelativePath(StringExtractor.RepositoryRoot(), script), [], TimeSpan.FromSeconds(120));
        Assert.True(exit == 0, output + errors);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(line => line.Contains('='))
            .ToDictionary(line => line[..line.IndexOf('=')], line => line[(line.IndexOf('=') + 1)..]);
    }

    static string Quote(string? text) => text is null ? "''" : "'" + text.Replace("'", "''") + "'";

    [Fact]
    public async Task Customer_releases_of_one_public_version_replace_each_other_in_release_number_order()
    {
        var result = await EvaluateAsync(
            ("later", Claim(revision: 1), "1.2.0.0", Claim(revision: 2), "1.2.0.0", false),
            ("same", Claim(revision: 2), "1.2.0.0", Claim(revision: 2), "1.2.0.0", false),
            ("older", Claim(revision: 3), "1.2.0.0", Claim(revision: 2), "1.2.0.0", false),
            ("laterversion", Claim("1.2.0", revision: 9), "1.2.0.0", Claim("1.2.1", revision: 1), "1.2.1.0", false),
            ("earlierversion", Claim("1.3.0", revision: 1), "1.3.0.0", Claim("1.2.9", revision: 5), "1.2.9.0", false),
            ("numeric", Claim("1.9.0", revision: 1), "1.9.0.0", Claim("1.10.0", revision: 1), "1.10.0.0", false));

        Assert.Equal("|True", result["later"]);
        Assert.Contains("already installed", result["same"]);
        Assert.Contains("older", result["older"]);
        Assert.Equal("|True", result["laterversion"]);
        Assert.Contains("older", result["earlierversion"]);
        Assert.Equal("|True", result["numeric"]);   // numbers, not text
    }

    [Fact]
    public async Task Force_allows_an_older_release_but_never_another_school_another_build_or_a_claim_that_is_not_valid()
    {
        var result = await EvaluateAsync(
            ("olderforced", Claim(revision: 3), "1.2.0.0", Claim(revision: 2), "1.2.0.0", true),
            ("school", Claim(revision: 3), "1.2.0.0", Claim(audience: "school-b", revision: 4), "1.2.0.0", true),
            ("anyschool", Claim(revision: 3), "1.2.0.0", Claim(audience: "*", revision: 4), "1.2.0.0", false),
            ("variant", Claim(revision: 3), "1.2.0.0", Claim(variant: "other", revision: 4), "1.2.0.0", true),
            ("garbage", Claim(revision: 3), "1.2.0.0", "cm-release/1|1.2|customer|a|windows|4|x", "1.2.0.0", true));

        Assert.Equal("|True", result["olderforced"]);
        Assert.Contains("another school", result["school"]);
        Assert.Equal("|True", result["anyschool"]);
        Assert.Contains("another kind of build", result["variant"]);
        Assert.Contains("not valid", result["garbage"]);
    }

    [Fact]
    public async Task A_program_without_identity_cannot_replace_a_customer_release_unless_forced_and_internal_builds_keep_the_file_version_rule()
    {
        var result = await EvaluateAsync(
            ("plain", Claim(revision: 3), "1.2.0.0", null, "1.2.0.900", false),
            ("plainforced", Claim(revision: 3), "1.2.0.0", null, "1.2.0.900", true),
            ("neither", null, "1.2.0.440", null, "1.2.0.443", false));

        Assert.Contains("no release identity", result["plain"]);
        Assert.Equal("|False", result["plainforced"]);   // not handled here: the file-version rule (forced) decides
        Assert.Equal("|False", result["neither"]);
    }

    [Fact]
    public async Task An_installation_from_before_release_identities_moves_to_a_customer_release_of_the_same_or_a_later_public_version()
    {
        var result = await EvaluateAsync(
            ("sameversion", null, "1.2.0.443", Claim("1.2.0", revision: 1), "1.2.0.0", false),
            ("laterversion", null, "1.2.0.443", Claim("1.2.1", revision: 1), "1.2.1.0", false),
            ("earlierversion", null, "1.3.0.12", Claim("1.2.9", revision: 1), "1.2.9.0", false),
            ("earlierpatch", null, "1.2.1.7", Claim("1.2.0", revision: 1), "1.2.0.0", false),
            ("forced", null, "1.3.0.12", Claim("1.2.9", revision: 1), "1.2.9.0", true));

        Assert.Equal("|True", result["sameversion"]);
        Assert.Equal("|True", result["laterversion"]);
        Assert.Contains("older", result["earlierversion"]);
        Assert.Contains("older", result["earlierpatch"]);
        Assert.Equal("|True", result["forced"]);
    }

    [Fact]
    public async Task The_claim_reader_returns_nothing_for_a_plain_program_and_a_marked_invalid_claim_for_a_bad_one()
    {
        Directory.CreateDirectory(_folder);
        var script = Path.Combine(_folder, "claims.ps1");
        File.WriteAllLines(script,
        [
            "$ErrorActionPreference = 'Stop'",
            $"Import-Module '{Path.Combine(StringExtractor.RepositoryRoot(), "deploy", "ClassroomSetup.psm1")}' -Force",
            "Write-Output ('none=' + ($null -eq (ConvertFrom-CmReleaseClaim '')))",
            "Write-Output ('trademark=' + ($null -eq (ConvertFrom-CmReleaseClaim 'Dausuva MB')))",
            "Write-Output ('bad=' + (ConvertFrom-CmReleaseClaim 'cm-release/1|1.2.0').Valid)",
            "Write-Output ('missingfile=' + ($null -eq (Get-CmReleaseClaim 'C:\\no\\such\\file.exe')))",
            "$c = ConvertFrom-CmReleaseClaim '" + Claim("1.2.0", "school-a", "windows", 7) + "'",
            "Write-Output ('good=' + $c.Valid + '/' + $c.PublicVersion + '/' + $c.Revision + '/' + $c.Audience)",
        ]);

        var (exit, output, errors) = await PowerShellScript.RunAsync(PowerShellEdition.Windows, Path.GetRelativePath(StringExtractor.RepositoryRoot(), script), [], TimeSpan.FromSeconds(60));

        Assert.True(exit == 0, output + errors);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["none=True", "trademark=True", "bad=False", "missingfile=True", "good=True/1.2.0/7/school-a"], lines);
    }

    [Fact]
    public async Task The_unattended_installer_repairs_only_the_same_release_and_leaves_every_other_release_to_the_order_check()
    {
        Directory.CreateDirectory(_folder);
        var script = Path.Combine(_folder, "actions.ps1");
        string Case(string name, bool installed, string installedVersion, string sourceVersion, string? installedClaim, string? sourceClaim) =>
            $"Write-Output ('{name}=' + (Get-CmBundledAction -Installed ${installed.ToString().ToLowerInvariant()} -InstalledVersion ([version]'{installedVersion}') -SourceVersion ([version]'{sourceVersion}') " +
            $"-InstalledClaim (ConvertFrom-CmReleaseClaim {Quote(installedClaim)}) -SourceClaim (ConvertFrom-CmReleaseClaim {Quote(sourceClaim)})))";
        File.WriteAllLines(script,
        [
            "$ErrorActionPreference = 'Stop'",
            $"Import-Module '{Path.Combine(StringExtractor.RepositoryRoot(), "deploy", "ClassroomSetup.psm1")}' -Force",
            Case("fresh", false, "0.0.0.0", "1.2.0.0", null, Claim(revision: 1)),
            Case("samerelease", true, "1.2.0.0", "1.2.0.0", Claim(revision: 2), Claim(revision: 2)),
            Case("newerrelease", true, "1.2.0.0", "1.2.0.0", Claim(revision: 1), Claim(revision: 2)),
            Case("olderrelease", true, "1.2.0.0", "1.2.0.0", Claim(revision: 2), Claim(revision: 1)),
            Case("legacytocustomer", true, "1.2.0.443", "1.2.0.0", null, Claim(revision: 1)),
            Case("customertoplain", true, "1.2.0.0", "1.2.0.443", Claim(revision: 1), null),
            Case("plainsame", true, "1.2.0.443", "1.2.0.443", null, null),
            Case("plainnewer", true, "1.2.0.440", "1.2.0.443", null, null),
        ]);

        var (exit, output, errors) = await PowerShellScript.RunAsync(PowerShellEdition.Windows, Path.GetRelativePath(StringExtractor.RepositoryRoot(), script), [], TimeSpan.FromSeconds(60));

        Assert.True(exit == 0, output + errors);
        var actions = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToDictionary(line => line[..line.IndexOf('=')], line => line[(line.IndexOf('=') + 1)..]);
        Assert.Equal("Install", actions["fresh"]);
        Assert.Equal("Repair", actions["samerelease"]);
        Assert.Equal("Update", actions["newerrelease"]);
        Assert.Equal("Update", actions["olderrelease"]);   // Update-CmClient / Update-CmHost refuse it; the old file-version rule would have repaired (downgraded) it
        Assert.Equal("Update", actions["legacytocustomer"]);
        Assert.Equal("Update", actions["customertoplain"]);
        Assert.Equal("Repair", actions["plainsame"]);      // unchanged for programs without identities
        Assert.Equal("Update", actions["plainnewer"]);
    }

    [Fact]
    public void The_unattended_entry_point_asks_the_module_which_action_to_take()
    {
        var entry = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "deploy", "Setup-Bundled.ps1"));

        Assert.Contains("Get-CmBundledAction", entry);
        Assert.DoesNotContain("$version -eq [version]$status.Version", entry);   // no second, older rule beside the module's
    }

    [Fact]
    public void Both_update_paths_of_the_setup_module_ask_the_release_order_before_the_file_version_rule()
    {
        var module = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "deploy", "ClassroomSetup.psm1"));

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(module, "Test-CmReleaseOrder -Installed").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(module, "-not \\$order.Handled").Count);
    }
}
