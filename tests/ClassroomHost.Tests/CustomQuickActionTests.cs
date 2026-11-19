// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class CustomQuickActionTests
{
    static readonly DateTime Monday = new(2026, 10, 12, 9, 15, 2);

    [Fact]
    public void Sync_copies_only_new_and_changed_files_like_rsync()
    {
        var remote = new[]
        {
            new RemoteFile("essay.docx", 100, Monday),
            new RemoteFile("same.txt", 5, Monday),
            new RemoteFile("bigger.txt", 9, Monday),
            new RemoteFile(@"sub\newer.txt", 5, Monday.AddMinutes(3)),
        };
        var local = new Dictionary<string, LocalFile>
        {
            ["same.txt"] = new(5, Monday.AddMilliseconds(400)),
            ["bigger.txt"] = new(4, Monday),
            [@"sub\newer.txt"] = new(5, Monday),
        };
        var copy = FolderSync.ToCopy(remote, path => local.GetValueOrDefault(path)).Select(file => file.RelativePath);
        Assert.Equal(["essay.docx", "bigger.txt", @"sub\newer.txt"], copy);
    }

    [Fact]
    public void Without_overwrite_a_sync_only_adds_missing_files()
    {
        var remote = new[] { new RemoteFile("essay.docx", 100, Monday), new RemoteFile("changed.txt", 9, Monday) };
        var local = new Dictionary<string, LocalFile> { ["changed.txt"] = new(4, Monday.AddDays(-1)) };
        var copy = FolderSync.ToCopy(remote, path => local.GetValueOrDefault(path), overwrite: false).Select(file => file.RelativePath);
        Assert.Equal(["essay.docx"], copy);
        // Quick actions saved before the setting existed keep overwriting changed files.
        Assert.True(JsonSerializer.Deserialize<CustomQuickAction>("""{"Kind":"sync-files"}""")!.Overwrite);
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"a\..\..\b.txt")]
    [InlineData("")]
    public void Sync_never_writes_outside_the_computer_folder(string path)
    {
        Assert.Empty(FolderSync.ToCopy([new RemoteFile(path, 1, Monday)], _ => null));
    }

    [Fact]
    public void The_change_time_the_client_sends_is_read_back_to_the_second()
    {
        Assert.Equal(Monday, FolderSync.ParseModified("2026-10-12T09:15:02"));
        Assert.Equal(DateTime.MinValue, FolderSync.ParseModified("not a date"));
    }

    [Theory]
    [InlineData(CustomQuickActionKinds.OpenProgram, "", "", "Enter the program to open.")]
    [InlineData(CustomQuickActionKinds.OpenProgram, "mspaint.exe", "", "")]
    [InlineData(CustomQuickActionKinds.OpenWebsite, "school.example.org", "", "Enter a web address that starts with http:// or https://.")]
    [InlineData(CustomQuickActionKinds.OpenWebsite, "https://school.example.org", "", "")]
    [InlineData(CustomQuickActionKinds.Message, "", "", "Enter the message.")]
    [InlineData(CustomQuickActionKinds.SyncFiles, "%DOCUMENTS%", "relative\\folder", "Choose the folder on this computer to sync into.")]
    [InlineData(CustomQuickActionKinds.SyncFiles, "%DOCUMENTS%", "D:\\Class work", "")]
    public void Each_kind_checks_its_own_settings(string kind, string target, string localFolder, string problem)
    {
        var action = new CustomQuickAction { Name = "Test", Kind = kind, Target = target, LocalFolder = localFolder };
        Assert.Equal(problem, action.Problem());
    }

    [Fact]
    public void A_power_action_needs_a_delay_Windows_accepts_and_every_action_a_name()
    {
        Assert.Equal("", new CustomQuickAction { Name = "Sleep", Kind = CustomQuickActionKinds.Power, PowerAction = "Sleep", DelaySeconds = 600 }.Problem());
        Assert.NotEqual("", new CustomQuickAction { Name = "Sleep", Kind = CustomQuickActionKinds.Power, DelaySeconds = 4000 }.Problem());
        Assert.Equal("Give the quick action a name.", new CustomQuickAction { Kind = CustomQuickActionKinds.Message, Text = "Hi" }.Problem());
        Assert.NotEqual("", new CustomQuickAction { Name = "Old", Kind = "teleport" }.Problem());
    }

    [Fact]
    public void Custom_quick_actions_are_saved_with_the_settings()
    {
        var settings = new HostSettings();
        settings.CustomQuickActions.Add(new CustomQuickAction { Name = "Collect homework", Kind = CustomQuickActionKinds.SyncFiles, Target = "%DOCUMENTS%\\Homework", LocalFolder = "D:\\Homework" });
        var back = JsonSerializer.Deserialize<HostSettings>(JsonSerializer.Serialize(settings))!;
        var action = Assert.Single(back.CustomQuickActions);
        Assert.Equal("Collect homework", action.Name);
        Assert.Equal("D:\\Homework", action.LocalFolder);
        Assert.Equal(settings.CustomQuickActions[0].Id, action.Id);
    }
}
