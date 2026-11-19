// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>What the Host window does first when it opens (T-253 phase 3), without a window.</summary>
public sealed class StartupRulesTests
{
    [Fact]
    public void A_remembered_computer_in_a_room_comes_back_not_yet_checked_with_the_name_of_the_key_it_used()
    {
        var settings = new HostSettings { AdditionalHostKeys = [new NamedHostKey { Id = "sci", Name = "Science lab", Psk = "science-lab-secret-key" }] };
        var keyId = SharedKey.Fingerprint("science-lab-secret-key");
        var saved = new SavedComputer { Address = "10.42.4.20", Name = "LAB1-PC20", Alias = "Anna's desk", ClientId = "abc123", Mac = "aa-bb-cc-dd-ee-ff", Port = 5050, Room = "Lab 1", KeyId = keyId };

        var client = StartupRules.ClientFor(saved, settings);

        Assert.Equal(("10.42.4.20", "LAB1-PC20", "Anna's desk", "abc123", 5050, "Lab 1"), (client.Address, client.ComputerName, client.Alias, client.ClientId, client.Port, client.Room));
        Assert.Equal(ComputerIdentity.NormalizeMac("aa-bb-cc-dd-ee-ff"), client.Mac);
        Assert.Equal(keyId, client.KeyId);
        Assert.Equal("Science lab", client.KeyName);
        Assert.Equal("Not checked", client.Status);
    }

    [Fact]
    public void A_remembered_computer_in_no_room_comes_back_as_only_discovered()
    {
        var client = StartupRules.ClientFor(new SavedComputer { Address = "10.42.4.21", Name = "PC21", Room = "" }, new HostSettings());
        Assert.Equal("Discovered - assign to a room to monitor", client.Status);
        Assert.Equal("", client.KeyName);
    }

    [Fact]
    public void An_exam_an_older_Host_saved_without_its_computers_covers_every_assigned_one_and_nothing_else_is_touched()
    {
        var settings = new HostSettings { ExamModeEnabled = true };
        ClientInfo[] clients =
        [
            new() { Address = "10.42.4.20", ClientId = "a", Room = "Lab 1" }, new() { Address = "10.42.4.21", ClientId = "b", Room = "" },
            new() { Address = "10.42.4.22", ClientId = "c", Room = "Lab 2" },
        ];

        Assert.True(StartupRules.CoverAssignedWhenUnrecorded(settings, clients));

        Assert.True(ExamModeScope.Contains(settings.ExamModeComputers, "a", "10.42.4.20"));
        Assert.True(ExamModeScope.Contains(settings.ExamModeComputers, "c", "10.42.4.22"));
        Assert.False(ExamModeScope.Contains(settings.ExamModeComputers, "b", "10.42.4.21"));
    }

    [Fact]
    public void An_exam_that_is_off_or_already_knows_its_computers_is_left_as_it_is()
    {
        ClientInfo[] clients = [new() { Address = "10.42.4.20", ClientId = "a", Room = "Lab 1" }];
        var off = new HostSettings { ExamModeEnabled = false };
        Assert.False(StartupRules.CoverAssignedWhenUnrecorded(off, clients));
        Assert.Empty(off.ExamModeComputers);

        var known = new HostSettings { ExamModeEnabled = true };
        ExamModeScope.Add(known.ExamModeComputers, "z", "10.42.4.99");
        var before = known.ExamModeComputers.ToList();
        Assert.False(StartupRules.CoverAssignedWhenUnrecorded(known, clients));
        Assert.Equal(before, known.ExamModeComputers);
    }

    [Theory]
    [InlineData(true, 0, StartupRules.FirstStep.None)]
    [InlineData(true, 12, StartupRules.FirstStep.None)]
    [InlineData(false, 0, StartupRules.FirstStep.SetupAssistant)]
    [InlineData(false, 3, StartupRules.FirstStep.SettingsDialog)]
    public void A_Host_with_nothing_is_led_through_the_first_steps_one_with_computers_but_no_key_is_taken_to_the_settings(bool hasKeys, int computers, StartupRules.FirstStep expected) =>
        Assert.Equal(expected, StartupRules.FirstStepFor(hasKeys, computers));
}
