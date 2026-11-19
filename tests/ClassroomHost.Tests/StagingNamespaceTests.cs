// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class StagingNamespaceTests
{
    [Fact]
    public void Remote_path_is_scoped_to_opaque_host_namespace()
    {
        var settings = new HostSettings
        {
            HostId = "abc12345",
            UploadToken = "0123456789abcdef0123456789abcdef",
        };

        Assert.Equal(
            @"C:\ProgramData\ClassroomManager\Staging\Hosts\abc12345-0123456789abcdef0123456789abcdef\install\Setup.msi",
            StagingNamespace.RemotePath(settings, @"install\Setup.msi"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"..\other\file.exe")]
    [InlineData(@"C:\Windows\file.exe")]
    public void Relative_path_rejects_empty_parent_and_rooted_values(string path)
    {
        Assert.Throws<ArgumentException>(() => StagingNamespace.NormalizeRelativePath(path));
    }
}
