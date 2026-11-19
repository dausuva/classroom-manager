// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class RemoteFileListingTests
{
    [Fact]
    public void Match_requires_same_file_name_and_byte_length()
    {
        var listing = new JsonObject
        {
            ["entries"] = new JsonArray
            {
                new JsonObject { ["name"] = "Setup.exe", ["isDir"] = false, ["size"] = 1234L },
                new JsonObject { ["name"] = "Setup", ["isDir"] = true, ["size"] = 0L },
            },
        };

        Assert.True(RemoteFileListing.Matches(listing, "setup.EXE", 1234));
        Assert.False(RemoteFileListing.Matches(listing, "Setup.exe", 1235));
        Assert.False(RemoteFileListing.Matches(listing, "Other.exe", 1234));
        Assert.False(RemoteFileListing.Matches(listing, "Setup", 0));
    }
}
