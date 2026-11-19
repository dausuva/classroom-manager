// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Reflection;
using ClassroomManager.Client;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-160: the helper neither reads nor rewrites the service's block lists.</summary>
public class HelperBlockHandlerTests
{
    [Fact]
    public void The_helpers_handler_starts_empty_and_leaves_the_saved_lists_alone()
    {
        var saved = Path.Combine(ClientConfig.DataDir, "blocked-websites.json");
        DateTime? before = File.Exists(saved) ? File.GetLastWriteTimeUtc(saved) : null;

        var handler = BlockHandler.ForHelper();

        Assert.False(handler.Persisted);
        Assert.Equal(0, handler.BlockedWebsiteCount);
        DateTime? after = File.Exists(saved) ? File.GetLastWriteTimeUtc(saved) : null;
        Assert.Equal(before, after);
    }

    [Fact]
    public void Every_block_and_exam_command_is_refused_by_the_helper()
    {
        var commands = typeof(Cmd).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && (field.Name.StartsWith("Block") || field.Name.StartsWith("Exam")))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(11, commands.Count);
        Assert.All(commands, command => Assert.True(BlockHandler.Handles(command), command));
    }

    [Fact]
    public void Other_commands_are_not_taken_for_block_commands()
    {
        Assert.False(BlockHandler.Handles(Cmd.ProcList));
        Assert.False(BlockHandler.Handles(Cmd.BrowserSites));
        Assert.False(BlockHandler.Handles("blocked"));
    }
}
