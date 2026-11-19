// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class WallpaperHistoryTests
{
    [Fact]
    public void Removing_closes_the_gap_and_keeps_the_order()
    {
        var history = new[] { @"C:\Users\pc\Pictures\a.jpg", @"C:\Users\pc\Downloads\B.png", "", @"C:\Windows\Web\Wallpaper\img0.jpg" };
        Assert.Equal([@"C:\Users\pc\Pictures\a.jpg", @"C:\Windows\Web\Wallpaper\img0.jpg"],
            WallpaperHistory.Without(history, [@"c:\users\pc\downloads\b.png", @"C:\not\there.jpg"]));
    }

    [Fact]
    public void An_image_survives_the_wire_and_the_commands_are_user_level_and_new_in_1_12()
    {
        var image = new WallpaperHistory.Image(@"C:\Users\pc\Pictures\a.jpg", true, 123456789012, true, "AAAA");
        var back = WallpaperHistory.ImagesFrom(JsonNode.Parse(new JsonObject
        {
            [WallpaperHistory.Images] = new JsonArray(image.ToJson()),
        }.ToJsonString())!.AsObject());
        Assert.Equal(image, Assert.Single(back));

        foreach (var command in new[] { Cmd.WallpaperRecentList, Cmd.WallpaperRecentRemove })
        {
            Assert.True(Cmd.IsUserOnly(command));
            Assert.Equal("1.12", Protocol.RequiredVersion(command));
            Assert.Contains(command, Protocol.Capabilities);
            Assert.Equal(OperatorRole.Teacher, CommandAccess.RequiredRole(command));
        }
    }

    [Fact]
    public void The_result_reads_as_one_line()
    {
        var data = new JsonObject { [WallpaperHistory.Removed] = 2, [WallpaperHistory.FilesDeleted] = 1, [WallpaperHistory.FilesKept] = 1 };
        Assert.Equal("2 image(s) removed from Recent images. 1 file(s) deleted. 1 file(s) kept: on the desktop now, outside the user's folder or not deletable.",
            WallpaperHistory.Describe(data));
    }
}
