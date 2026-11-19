// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

[Collection(nameof(HostBackupTests))]
public sealed class HostStartupProbeTests
{
    [Fact]
    public async Task Actual_Host_composes_without_display_or_profile_changes_and_rejects_wrong_versions()
    {
        var executable = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
        var (exit, output, errors) = await PowerShellScript.RunAsync(PowerShellEdition.Windows,
            "tests/ClassroomHost.Tests/HostStartupProbe.Tests.ps1", ["-Executable", executable], TimeSpan.FromMinutes(2));
        Assert.True(exit == 0, output + errors);
        Assert.Contains("PASS: actual Host startup", output);
    }
}
