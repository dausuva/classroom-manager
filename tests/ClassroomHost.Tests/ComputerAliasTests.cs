// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.ComponentModel;
using System.Text.Json;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-171: a label only the Host knows, kept apart from the computer's Windows name.</summary>
public sealed class ComputerAliasTests
{
    [Fact]
    public void The_label_is_what_the_host_calls_the_computer()
    {
        var computer = new ClientInfo { ComputerName = "LAB1-PC07", Alias = "Front row, left" };

        Assert.Equal("Front row, left", computer.Name);
        Assert.Equal("LAB1-PC07", computer.ComputerName);
    }

    [Fact]
    public void Without_a_label_the_windows_name_shows()
    {
        var computer = new ClientInfo { ComputerName = "LAB1-PC07" };

        Assert.Equal("LAB1-PC07", computer.Name);
    }

    [Fact]
    public void A_name_the_computer_reports_never_replaces_the_label()
    {
        var computer = new ClientInfo { ComputerName = "OLD", Alias = "Teacher desk" };

        computer.Name = "NEW";

        Assert.Equal("Teacher desk", computer.Name);
        Assert.Equal("NEW", computer.ComputerName);
    }

    [Fact]
    public void Clearing_the_label_brings_the_windows_name_back_and_lists_are_told()
    {
        var computer = new ClientInfo { ComputerName = "PC1", Alias = "Desk" };
        var changed = new List<string?>();
        ((INotifyPropertyChanged)computer).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        computer.Alias = "  ";

        Assert.Equal("PC1", computer.Name);
        Assert.Contains("Name", changed);
    }

    [Theory]
    [InlineData("  Desk  ", "Desk")]
    [InlineData("Front\r\nrow\tleft", "Front row left")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void A_label_is_one_tidy_line(string? typed, string expected)
    {
        Assert.Equal(expected, ComputerLabel.Clean(typed));
    }

    [Fact]
    public void A_long_label_is_cut()
    {
        Assert.Equal(ComputerLabel.MaxLength, ComputerLabel.Clean(new string('x', 200)).Length);
    }

    [Fact]
    public void The_saved_computer_keeps_the_label_and_the_windows_name_apart()
    {
        var saved = new SavedComputer { Name = "PC1", Alias = "Desk", Address = "10.0.0.2" };

        var back = JsonSerializer.Deserialize<SavedComputer>(JsonSerializer.Serialize(saved))!;

        Assert.Equal("PC1", back.Name);
        Assert.Equal("Desk", back.Alias);
        Assert.Equal("", JsonSerializer.Deserialize<SavedComputer>("""{"Name":"PC1"}""")!.Alias);
    }
}
