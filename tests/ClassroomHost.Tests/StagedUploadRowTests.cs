// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class StagedUploadRowTests
{
    [Fact]
    public void Bulk_install_requires_supported_extension_presence_and_equal_size_on_every_target()
    {
        var first = new ClientInfo { Name = "PC-1", Address = "10.0.0.1" };
        var second = new ClientInfo { Name = "PC-2", Address = "10.0.0.2" };
        var synchronized = new StagedUploadRow
        {
            RelativePath = @"install\Setup.msi",
            Entries =
            [
                new StagedUploadPresence(first, @"install\Setup.msi", 42, default),
                new StagedUploadPresence(second, @"install\Setup.msi", 42, default),
            ],
        };

        Assert.True(synchronized.CanInstallOnAll(2));
        Assert.False(new StagedUploadRow
        {
            RelativePath = @"install\Setup.msi",
            Entries = [new StagedUploadPresence(first, @"install\Setup.msi", 42, default)],
        }.CanInstallOnAll(2));
        Assert.False(new StagedUploadRow
        {
            RelativePath = @"install\Setup.exe",
            Entries =
            [
                new StagedUploadPresence(first, @"install\Setup.exe", 42, default),
                new StagedUploadPresence(second, @"install\Setup.exe", 43, default),
            ],
        }.CanInstallOnAll(2));
        Assert.False(new StagedUploadRow
        {
            RelativePath = @"install\notes.txt",
            Entries =
            [
                new StagedUploadPresence(first, @"install\notes.txt", 42, default),
                new StagedUploadPresence(second, @"install\notes.txt", 42, default),
            ],
        }.CanInstallOnAll(2));
    }

    [Fact]
    public void Upload_progress_is_byte_weighted_monotonic_and_bounded()
    {
        var progress = new StagedUploadProgressTracker(
        [
            new StagedUploadOperation("small", 10),
            new StagedUploadOperation("large", 90),
        ]);

        Assert.Equal(0.05, progress.Report("small", 0.5), 6);
        Assert.Equal(0.95, progress.Report("large", 1), 6);
        Assert.Equal(0.95, progress.Report("large", 0.25), 6);
        Assert.Equal(1, progress.Report("small", 2), 6);
    }
}
