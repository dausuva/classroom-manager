// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ClientUpdateRecoveryScriptTests : IDisposable
{
    static readonly string RecoveryScript = Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomClient", "UpdateRecovery.ps1");
    readonly string _directory = Directory.CreateTempSubdirectory("cm update recovery ").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Healthy_candidate_is_accepted_and_recovery_reservation_is_released()
    {
        var result = await Run("success", "0", mutateFiles: false, failCandidateCopy: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("new signed candidate", result.Target);
        Assert.Equal("original config", result.Config);
        Assert.Equal("original certificate", result.Certificate);
        Assert.False(result.ActiveMarkerExists, result.Log);
        Assert.False(result.UploadedExists);
        Assert.Contains("Update accepted", result.Log);
    }

    [Fact]
    public async Task Failed_new_service_restores_previous_executable_configuration_and_certificate()
    {
        var result = await Run("health failure", "1,0", mutateFiles: true, failCandidateCopy: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("previous signed executable", result.Target);
        Assert.Equal("original config", result.Config);
        Assert.Equal("original certificate", result.Certificate);
        Assert.False(result.ActiveMarkerExists, result.Log);
        Assert.Contains("ROLLBACK SUCCEEDED", result.Log);
    }

    [Fact]
    public async Task Candidate_judges_itself_but_the_restored_previous_executable_judges_the_rollback()
    {
        // A candidate that cannot run the probe at all (the 2026-10-31 ACER drill) must not veto the rollback of the build that works.
        var result = await Run("verifier choice", "1,0", mutateFiles: false, failCandidateCopy: false);

        Assert.Equal(["candidate.exe", "ClassroomClient.exe"], result.Verifiers);
        Assert.Contains("ROLLBACK SUCCEEDED", result.Log);
    }

    [Fact]
    public async Task Installed_path_with_spaces_reaches_the_health_probe_as_one_argument()
    {
        // Start-Process does not quote list items; an install under "Program Files" was split into several arguments and refused (exit 2).
        var result = await Run("quoted target", "0", mutateFiles: false, failCandidateCopy: false);

        var target = Path.Combine(_directory, "quoted target", "ClassroomClient.exe");
        Assert.Contains(' ', target);
        Assert.All(result.Arguments, line => Assert.Contains($"\"{target}\"", line));
    }

    [Fact]
    public async Task Previous_executable_that_stays_installed_after_a_failed_copy_is_also_verified_by_itself()
    {
        var result = await Run("copy failure verifier", "0", mutateFiles: false, failCandidateCopy: true);

        Assert.Equal(["ClassroomClient.exe"], result.Verifiers);
    }

    [Fact]
    public async Task Failed_copy_keeps_old_image_and_restarts_it()
    {
        var result = await Run("copy failure", "0", mutateFiles: false, failCandidateCopy: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("previous signed executable", result.Target);
        Assert.False(result.ActiveMarkerExists);
        Assert.Contains("Previous Client remains installed and passed authenticated service health", result.Log);
    }

    [Fact]
    public async Task Failed_candidate_and_failed_rollback_retain_the_recovery_marker_and_backups()
    {
        var result = await Run("rollback failure", "1,1", mutateFiles: false, failCandidateCopy: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("previous signed executable", result.Target);
        Assert.True(result.ActiveMarkerExists, result.Log);
        Assert.True(File.Exists(Path.Combine(result.Transaction, "previous.exe")));
        Assert.Contains("CRITICAL", result.Log);
    }

    async Task<RunResult> Run(string name, string healthCodes, bool mutateFiles, bool failCandidateCopy)
    {
        var transaction = Path.Combine(_directory, name);
        var data = Path.Combine(transaction, "data");
        Directory.CreateDirectory(data);
        var target = Path.Combine(transaction, "ClassroomClient.exe");
        var candidate = Path.Combine(transaction, "candidate.exe");
        var previous = Path.Combine(transaction, "previous.exe");
        var previousConfig = Path.Combine(transaction, "client.json");
        var previousCertificate = Path.Combine(transaction, "tls-cert.pfx");
        var uploaded = Path.Combine(transaction, "uploaded.exe");
        var active = Path.Combine(transaction, "active.json");
        var log = Path.Combine(transaction, "update.log");
        var driver = Path.Combine(transaction, "driver.ps1");
        var output = Path.Combine(transaction, "result.json");

        await File.WriteAllTextAsync(target, "previous signed executable");
        await File.WriteAllTextAsync(candidate, "new signed candidate");
        await File.WriteAllTextAsync(previous, "previous signed executable");
        await File.WriteAllTextAsync(Path.Combine(data, "client.json"), "original config");
        await File.WriteAllTextAsync(Path.Combine(data, "tls-cert.pfx"), "original certificate");
        File.Copy(Path.Combine(data, "client.json"), previousConfig);
        File.Copy(Path.Combine(data, "tls-cert.pfx"), previousCertificate);
        await File.WriteAllTextAsync(uploaded, "uploaded candidate");
        await File.WriteAllTextAsync(active, "reserved");
        var candidateHash = Hash(candidate);
        var previousHash = Hash(previous);
        var configHash = Hash(previousConfig);
        var certificateHash = Hash(previousCertificate);

        await File.WriteAllTextAsync(driver, Driver);
        var arguments = new[]
        {
            "-RecoveryScript", RecoveryScript, "-Transaction", transaction, "-ActiveMarker", active,
            "-Candidate", candidate, "-PreviousExecutable", previous, "-PreviousConfig", previousConfig,
            "-PreviousCertificate", previousCertificate, "-Target", target, "-ExpectedSha256", candidateHash,
            "-PreviousSha256", previousHash, "-ConfigSha256", configHash, "-CertificateSha256", certificateHash,
            "-CandidateVersion", "2.0.0.0", "-PreviousVersion", "1.0.0.0", "-LogFile", log,
            "-Uploaded", uploaded, "-DataDirectory", data, "-Output", output,
            "-CandidateHealthSeconds", "0", "-RecoveryHealthSeconds", "0",
            "-HealthCodes", healthCodes, "-MutateFiles", mutateFiles.ToString(), "-FailCandidateCopy", failCandidateCopy.ToString(),
        };

        var (exit, stdout, stderr) = await PowerShellScript.RunAsync(PowerShellEdition.Windows, driver, arguments, TimeSpan.FromSeconds(60));
        Assert.True(exit == 0, stdout + stderr);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        var root = result.RootElement;
        return new RunResult(root.GetProperty("ExitCode").GetInt32(),
            await File.ReadAllTextAsync(target), await File.ReadAllTextAsync(Path.Combine(data, "client.json")),
            await File.ReadAllTextAsync(Path.Combine(data, "tls-cert.pfx")), File.Exists(active), File.Exists(uploaded),
            await File.ReadAllTextAsync(log), transaction,
            root.GetProperty("Verifiers").EnumerateArray().Select(v => Path.GetFileName(v.GetString()!)).ToArray(),
            root.GetProperty("Arguments").EnumerateArray().Select(v => v.GetString()!).ToArray());
    }

    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    sealed record RunResult(int ExitCode, string Target, string Config, string Certificate, bool ActiveMarkerExists, bool UploadedExists, string Log, string Transaction, string[] Verifiers, string[] Arguments);

    const string Driver = """
        param($RecoveryScript,$Transaction,$ActiveMarker,$Candidate,$PreviousExecutable,$PreviousConfig,$PreviousCertificate,
          $Target,$ExpectedSha256,$PreviousSha256,$ConfigSha256,$CertificateSha256,$CandidateVersion,$PreviousVersion,
          $LogFile,$Uploaded,$DataDirectory,$CandidateHealthSeconds,$RecoveryHealthSeconds,$Output,$HealthCodes,$MutateFiles,$FailCandidateCopy)
        class FakeUpdateProcess {
          [int]$ExitCode
          [bool]$HasExited = $true
          FakeUpdateProcess([int]$code) { $this.ExitCode = $code }
          [bool]WaitForExit([int]$timeout) { return $true }
          [void]Kill([bool]$entireTree) { }
        }
        $global:ServiceState = 'Running'
        $global:HealthQueue = [Collections.Queue]::new()
        foreach ($code in ($HealthCodes -split ',')) { $global:HealthQueue.Enqueue([int]$code) }
        $global:DidMutate = $false
        $global:Verifiers = @()
        $global:Arguments = @()
        function Get-Service { [CmdletBinding()]param([string]$Name) [pscustomobject]@{ Status = $global:ServiceState } }
        function Stop-Service { [CmdletBinding()]param([string]$Name,[switch]$Force) $global:ServiceState = 'Stopped' }
        function Start-Service { [CmdletBinding()]param([string]$Name) $global:ServiceState = 'Running' }
        function Get-Process { [CmdletBinding()]param([string]$Name) @() }
        function Stop-Process { [CmdletBinding()]param([switch]$Force) }
        function Start-Sleep { [CmdletBinding()]param([int]$Milliseconds,[int]$Seconds) }
        function icacls.exe { $global:LASTEXITCODE = 0 }
        function Start-Process {
          [CmdletBinding()]param([string]$FilePath,[string[]]$ArgumentList,[string]$WindowStyle,[switch]$PassThru)
          $global:Verifiers += $FilePath
          $global:Arguments += ($ArgumentList -join ' ')
          $code = if ($global:HealthQueue.Count -gt 0) { $global:HealthQueue.Dequeue() } else { 1 }
          if ($MutateFiles -eq 'True' -and $code -ne 0 -and -not $global:DidMutate) {
            [IO.File]::WriteAllText((Join-Path $DataDirectory 'client.json'), 'modified config')
            [IO.File]::WriteAllText((Join-Path $DataDirectory 'tls-cert.pfx'), 'modified certificate')
            $global:DidMutate = $true
          }
          return [FakeUpdateProcess]::new([int]$code)
        }
        function Copy-Item {
          [CmdletBinding()]param([string]$LiteralPath,[string]$Destination,[switch]$Force)
          if ($FailCandidateCopy -eq 'True' -and $LiteralPath -eq $Candidate -and $Destination -eq $Target) { throw 'synthetic copy failure' }
          Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
        }
        $parameters = @{
          Transaction=$Transaction; ActiveMarker=$ActiveMarker; Candidate=$Candidate; PreviousExecutable=$PreviousExecutable
          PreviousConfig=$PreviousConfig; PreviousCertificate=$PreviousCertificate; Target=$Target
          ExpectedSha256=$ExpectedSha256; PreviousSha256=$PreviousSha256; ConfigSha256=$ConfigSha256
          CertificateSha256=$CertificateSha256; CandidateVersion=$CandidateVersion; PreviousVersion=$PreviousVersion
          LogFile=$LogFile; Uploaded=$Uploaded; DataDirectory=$DataDirectory
          CandidateHealthSeconds=[int]$CandidateHealthSeconds; RecoveryHealthSeconds=[int]$RecoveryHealthSeconds
        }
        $code = . $RecoveryScript @parameters
        [ordered]@{ExitCode=$code;Service=$global:ServiceState;Verifiers=@($global:Verifiers);Arguments=@($global:Arguments)}|ConvertTo-Json|Set-Content -LiteralPath $Output
        """;
}
