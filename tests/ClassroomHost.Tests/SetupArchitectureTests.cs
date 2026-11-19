// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>The setup module chooses the runtime, the installer and the refusal by the architecture of the build (x64 or x86).</summary>
public sealed class SetupArchitectureTests
{
    [Theory]
    [InlineData("Names")]
    [InlineData("SourceArchitecture")]
    [InlineData("WrongWindows")]
    [InlineData("RuntimeFolder")]
    public async Task The_setup_module_follows_the_architecture_of_the_build(string scenario)
    {
        // The setup module runs on school PCs, which have Windows PowerShell only.
        var (exit, output, errors) = await PowerShellScript.RunAsync(PowerShellEdition.Windows, "tests/ClassroomHost.Tests/SetupArchitecture.Tests.ps1",
            ["-RepositoryRoot", StringExtractor.RepositoryRoot(), "-Scenario", scenario], TimeSpan.FromSeconds(60));
        Assert.True(exit == 0, output + errors);
        Assert.Contains($"PASS: {scenario}", output);
    }
}
