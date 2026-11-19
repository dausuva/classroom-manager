// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class HostUpdateRecoveryScriptTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("copy-failure")]
    [InlineData("restore-failure")]
    [InlineData("restore-delete-failure")]
    [InlineData("missing-backup")]
    [InlineData("nonterminating-copy")]
    [InlineData("startup-failure")]
    [InlineData("startup-timeout")]
    [InlineData("unsupported-source")]
    public async Task Setup_recovers_copy_failures_and_preserves_failed_rollback_evidence(string scenario)
    {
        var scratch = Directory.CreateTempSubdirectory("cm-host-update-").FullName;
        try
        {
            var (exit, output, errors) = await PowerShellScript.RunAsync(PowerShellEdition.Windows,
                "tests/ClassroomHost.Tests/HostUpdateRecovery.Tests.ps1",
                ["-RepositoryRoot", StringExtractor.RepositoryRoot(), "-Scratch", scratch, "-Scenario", scenario],
                TimeSpan.FromSeconds(30));
            Assert.True(exit == 0, output + errors);
            Assert.Contains("PASS: " + scenario, output);
        }
        finally { Directory.Delete(scratch, true); }
    }
}
