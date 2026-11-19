// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using Xunit;

namespace ClassroomHost.Tests;

public sealed class DiagnosePrivacyTests
{
    [Theory]
    [InlineData("Nested")]
    [InlineData("Malformed")]
    [InlineData("InvalidTypes")]
    [InlineData("IncludeLogs")]
    [InlineData("Legacy")]
    [InlineData("RootArray")]
    [InlineData("Empty")]
    public async Task Packaged_diagnostics_only_export_allowlisted_settings_and_explicit_raw_logs(string scenario)
    {
        // deploy\tools\Diagnose.ps1 is in the setup kit and runs on school PCs, which have Windows PowerShell only.
        var (exit, output, errors) = await PowerShellScript.RunAsync(PowerShellEdition.Windows, "tests/ClassroomHost.Tests/DiagnosePrivacy.Tests.ps1",
            ["-RepositoryRoot", StringExtractor.RepositoryRoot(), "-Scenario", scenario], TimeSpan.FromSeconds(45));
        Assert.True(exit == 0, output + errors);
        Assert.Contains($"PASS: {scenario}", output);
    }
}
