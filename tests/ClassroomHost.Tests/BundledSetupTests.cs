// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class BundledSetupTests
{
    [Theory]
    [InlineData(PowerShellEdition.Windows, "Client", false, "Install")]
    [InlineData(PowerShellEdition.Core, "Client", false, "Install")]
    [InlineData(PowerShellEdition.Windows, "Host", false, "Install")]
    [InlineData(PowerShellEdition.Core, "Host", false, "Install")]
    [InlineData(PowerShellEdition.Windows, "Client", true, "Repair")]
    [InlineData(PowerShellEdition.Core, "Host", true, "Repair")]
    public async Task Unattended_setup_selects_install_or_same_version_repair_without_prompts(
        PowerShellEdition edition, string component, bool installed, string action)
    {
        using var fixture = new Fixture(component, installed, key: true);
        var (exit, output, errors) = await fixture.Run(edition);
        Assert.True(exit == 0, output + errors);
        using var json = JsonDocument.Parse(File.ReadAllText(fixture.Record));
        Assert.Equal(action, json.RootElement.GetProperty("Action").GetString());
        Assert.Equal(component, json.RootElement.GetProperty("Component").GetString());
        Assert.Equal("Never", json.RootElement.GetProperty("RuntimeDownload").GetString());
        Assert.True(json.RootElement.GetProperty("Quiet").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("Key", out _));
        Assert.False(json.RootElement.TryGetProperty("Force", out _));
    }

    [Theory]
    [MemberData(nameof(PowerShellScript.Both), MemberType = typeof(PowerShellScript))]
    public async Task A_fresh_client_without_an_explicit_kit_key_is_refused_before_setup(PowerShellEdition edition)
    {
        using var fixture = new Fixture("Client", installed: false, key: false);
        var (exit, _, _) = await fixture.Run(edition);
        Assert.Equal(1, exit);
        Assert.False(File.Exists(fixture.Record));
    }

    [Theory]
    [MemberData(nameof(PowerShellScript.Both), MemberType = typeof(PowerShellScript))]
    public async Task A_different_version_uses_update_and_propagates_failure_without_force(PowerShellEdition edition)
    {
        using var fixture = new Fixture("Client", installed: true, key: false, version: "0.0.0.1", result: 23);
        var (exit, _, _) = await fixture.Run(edition);
        Assert.Equal(23, exit);
        using var json = JsonDocument.Parse(File.ReadAllText(fixture.Record));
        Assert.Equal("Update", json.RootElement.GetProperty("Action").GetString());
        Assert.False(json.RootElement.TryGetProperty("Force", out _));
    }

    sealed class Fixture : IDisposable
    {
        readonly string _folder = Path.Combine(Path.GetTempPath(), "cm-bundled setup-" + Guid.NewGuid().ToString("N"));
        readonly string _component;
        public string Record => Path.Combine(_folder, "called.json");
        public Fixture(string component, bool installed, bool key, string? version = null, int result = 0)
        {
            Directory.CreateDirectory(_folder);
            _component = component;
            File.Copy(Path.Combine(StringExtractor.RepositoryRoot(), "deploy", "Setup-Bundled.ps1"), Path.Combine(_folder, "Setup-Bundled.ps1"));
            File.Copy(typeof(BundledSetupTests).Assembly.Location, Path.Combine(_folder, $"Classroom{component}.exe"));
            version ??= System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(BundledSetupTests).Assembly.Location).FileVersion!;
            File.WriteAllText(Path.Combine(_folder, "ClassroomSetup.psm1"), $$"""
                function Get-CmClientStatus { [pscustomobject]@{ Installed=${{installed.ToString().ToLowerInvariant()}}; Version='{{version}}'; InstallDir='C:\NoSuchInstall' } }
                function Get-CmReleaseClaim { param($Path) $null }
                function Get-CmBundledAction { param([bool]$Installed, [version]$InstalledVersion, [version]$SourceVersion, $InstalledClaim, $SourceClaim) if (-not $Installed) { 'Install' } elseif ($SourceVersion -eq $InstalledVersion) { 'Repair' } else { 'Update' } }
                function Get-CmHostStatus { Get-CmClientStatus }
                function Get-CmKitKey { {{(key ? "'synthetic-key-for-test'" : "$null")}} }
                function Test-CmKey { param($Value) if ($Value -ne 'synthetic-key-for-test') { 'Invalid synthetic key' } }
                """);
            File.WriteAllText(Path.Combine(_folder, "Setup.ps1"), $$"""
                param($Component,$Action,$SourceDir,[switch]$Quiet,$RuntimeDownload,$Language,$Key,[switch]$Force)
                $record = @{} + $PSBoundParameters
                $record['Quiet'] = [bool]$Quiet
                $record | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'called.json')
                exit {{result}}
                """);
        }
        public Task<(int ExitCode, string Output, string Errors)> Run(PowerShellEdition edition) =>
            PowerShellScript.RunAsync(edition, Path.Combine(_folder, "Setup-Bundled.ps1"),
                ["-Component", _component, "-SourceDir", _folder, "-Language", "en"], TimeSpan.FromSeconds(60));
        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }
}
