// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>T-234/T-235: a program can tell whether Windows runs it from an MSIX package, so it does not install or update itself then.</summary>
public sealed class PackageIdentityTests
{
    [Fact]
    public void A_test_process_has_no_package_identity()
    {
        Assert.False(PackageIdentity.IsPackaged);
        Assert.Null(PackageIdentity.FullName);
    }

    [Theory]
    [InlineData("Dausuva.ClassroomHost_1.2.0.0_x64__8wekyb3d8bbwe", "Dausuva.ClassroomHost")]
    [InlineData("Dausuva.ClassroomClient_1.2.0.0_x64__abc", "Dausuva.ClassroomClient")]
    [InlineData("NoUnderscore", "NoUnderscore")]
    public void The_short_name_is_the_package_name_without_version_architecture_and_publisher_hash(string full, string expected) =>
        Assert.Equal(expected, PackageIdentity.ShortName(full));
}
