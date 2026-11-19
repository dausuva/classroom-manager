// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ConnectedAccountInventoryTests
{
    [Fact]
    public void Response_is_parsed_without_secret_values_and_targets_are_normalized()
    {
        var data = new JsonObject
        {
            ["user"] = "Student",
            ["identity"] = "LAB\\Student",
            ["microsoftAccountConnected"] = false,
            ["credentials"] = new JsonArray
            {
                new JsonObject { ["target"] = "LegacyGeneric:target=Teams" },
                new JsonObject { ["target"] = "legacygeneric:target=teams" },
                new JsonObject { ["target"] = "MicrosoftAccount:user=user@example.test" },
            },
        };

        var result = ConnectedAccountInventory.FromResponse("PC-01", data);

        Assert.Equal("Available", result.State);
        Assert.Equal("Student", result.User);
        Assert.Equal("LAB\\Student", result.Identity);
        Assert.Equal("Not detected", result.MicrosoftAccountText);
        Assert.Equal(2, result.CredentialTargets.Count);
    }

    [Fact]
    public void No_user_and_unsupported_results_remain_distinct()
    {
        var noUser = ConnectedAccountInventory.Unavailable("PC-01", "No user is signed in (the user-level helper is not running on this computer).");
        var unsupported = ConnectedAccountInventory.Unavailable("PC-02", "Protocol update required.", true);

        Assert.Equal("No signed-in user", noUser.State);
        Assert.Equal("Unsupported", unsupported.State);
        Assert.Equal("Unknown", unsupported.MicrosoftAccountText);
    }
}
