// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>Teacher mode never offers an action the computers refuse to a Teacher key.</summary>
public sealed class TeacherAdminActionsTests
{
    /// <summary>Actions that send nothing to a computer: they work on this Host alone.</summary>
    static readonly HashSet<string> HostOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "computers.scan", "computers.add", "computers.remove", "computers.remove-offline", "computers.export", "computers.import",
        "computers.wake", "computers.assign-room", "computers.columns", "computers.feedback", "restrictions.exam-settings",
        "settings.custom-quick-actions", "settings.arrange-toolbar", "settings.host", "settings.interface", "settings.languages",
        "settings.help", "settings.message-to-admin", "settings.messages-from-teachers", "settings.setup-assistant",
        "settings.command-favourites", "settings.crash-logs", "settings.lab-keys", "settings.export-teacher",
        "settings.export-configuration", "settings.import-configuration", "settings.backups", "settings.import-teacher",
        "settings.schedules", "settings.about", "settings.licenses", "settings.feedback",
    };

    [Fact]
    public void Every_action_names_the_command_it_sends_or_works_on_this_Host_alone()
    {
        var unknown = TeacherCommandCatalog.All
            .Where(command => !TeacherCommandCatalog.Sends.ContainsKey(command.Id) && !HostOnly.Contains(command.Id))
            .Select(command => command.Id).ToList();
        Assert.True(unknown.Count == 0, "Name the client command in TeacherCommandCatalog.Sends, or add the action to HostOnly here: " + string.Join(", ", unknown));
        Assert.All(TeacherCommandCatalog.Sends.Keys, id => Assert.Contains(TeacherCommandCatalog.All, command => command.Id == id));
    }

    [Theory]
    [InlineData("remote.uninstall-client", true)]
    [InlineData("remote.command", true)]
    [InlineData("restrictions.signed-in-accounts", true)]
    [InlineData("classroom.mouse", true)]
    [InlineData("classroom.lock", false)]
    [InlineData("remote.power", false)]
    [InlineData("classroom.collect-files", false)]
    [InlineData("settings.arrange-toolbar", false)]
    public void An_action_needs_an_Admin_key_when_its_command_does(string id, bool admin) =>
        Assert.Equal(admin, TeacherCommandCatalog.NeedsAdminKey(id));

    [Fact]
    public void The_client_removal_is_new_in_1_14_and_needs_an_Admin_key()
    {
        Assert.Equal("1.14", Protocol.RequiredVersion(Cmd.ClientUninstall));
        Assert.Contains(Cmd.ClientUninstall, Protocol.Capabilities);
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(Cmd.ClientUninstall));
        Assert.False(Cmd.IsUserOnly(Cmd.ClientUninstall));
    }
}
