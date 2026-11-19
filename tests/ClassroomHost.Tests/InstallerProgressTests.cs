// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class InstallerProgressTests
{
    [Theory]
    [InlineData(65, "1:05")]
    [InlineData(3661, "1:01:01")]
    public void FormatElapsed_uses_compact_clock_format(int seconds, string expected)
    {
        Assert.Equal(expected, InstallerProgress.FormatElapsed(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void RunningStatus_explains_that_installers_do_not_expose_a_percentage()
    {
        var status = InstallerProgress.RunningStatus("setup.exe", TimeSpan.FromSeconds(75));

        Assert.Contains("setup.exe", status);
        Assert.Contains("1:15", status);
        Assert.Contains("does not report percent", status);
    }
}
