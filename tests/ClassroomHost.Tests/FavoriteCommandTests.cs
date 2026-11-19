// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class FavoriteCommandTests
{
    [Fact]
    public void Normalize_removes_incomplete_entries_and_canonicalizes_values()
    {
        var commands = FavoriteCommandCatalog.Normalize(
        [
            new FavoriteCommand { Name = "  Check disks  ", Command = "  Get-Volume  ", Shell = "POWERSHELL", Level = "user" },
            new FavoriteCommand { Name = "", Command = "hostname" },
            new FavoriteCommand { Name = "Missing command", Command = " " },
            new FavoriteCommand { Name = "Version", Command = "ver", Shell = "unknown", Level = "unknown" },
        ]);

        Assert.Equal(2, commands.Count);
        Assert.Equal("Check disks", commands[0].Name);
        Assert.Equal("Get-Volume", commands[0].Command);
        Assert.Equal(FavoriteCommandShells.PowerShell, commands[0].Shell);
        Assert.Equal(Levels.User, commands[0].Level);
        Assert.Equal(FavoriteCommandShells.CommandPrompt, commands[1].Shell);
        Assert.Equal(Levels.System, commands[1].Level);
    }

    [Fact]
    public void Normalize_preserves_order_and_replaces_duplicate_ids()
    {
        var commands = FavoriteCommandCatalog.Normalize(
        [
            new FavoriteCommand { Id = "same", Name = "First", Command = "one" },
            new FavoriteCommand { Id = "SAME", Name = "Second", Command = "two" },
        ]);

        Assert.Equal(["First", "Second"], commands.Select(x => x.Name));
        Assert.Equal("same", commands[0].Id);
        Assert.False(commands[0].Id.Equals(commands[1].Id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpsertByName_adds_new_and_updates_existing_favourites()
    {
        var existing = new FavoriteCommand { Id = "keep-id", Name = "Version", Command = "ver" };

        var added = FavoriteCommandCatalog.UpsertByName([existing],
            new FavoriteCommand { Name = "Processes", Command = "Get-Process", Shell = FavoriteCommandShells.PowerShell });
        Assert.Equal(["Version", "Processes"], added.Select(item => item.Name));

        var updated = FavoriteCommandCatalog.UpsertByName(added,
            new FavoriteCommand { Name = " version ", Command = "Get-ComputerInfo", Shell = FavoriteCommandShells.PowerShell, Level = Levels.User });
        Assert.Equal(2, updated.Count);
        Assert.Equal("keep-id", updated[0].Id);
        Assert.Equal("Get-ComputerInfo", updated[0].Command);
        Assert.Equal(FavoriteCommandShells.PowerShell, updated[0].Shell);
        Assert.Equal(Levels.User, updated[0].Level);
    }
}
