// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class MainActionAvailabilityTests
{
    [Theory]
    [InlineData("Send files...", 0, false)]
    [InlineData("Send files...", 2, true)]
    [InlineData("Terminal...", 0, false)]
    [InlineData("Terminal...", 1, true)]
    [InlineData("Terminal...", 2, false)]
    [InlineData("Signed-in accounts...", 1, true)]
    [InlineData("Signed-in accounts...", 2, true)]
    [InlineData("Scan network", 0, true)]
    [InlineData("Install / uninstall...", 0, true)]
    public void Commands_reflect_their_target_requirements(string label, int selected, bool expected)
    {
        Assert.Equal(expected, MainActionAvailability.CanExecute(label, selected));
    }

    [Theory]
    [InlineData("classroom.lock", 0, "Select at least one computer.")]
    [InlineData("remote.terminal", 2, "Select exactly one computer.")]
    [InlineData("remote.terminal", 1, "")]
    [InlineData("custom:test", 0, "Select at least one computer.")]
    [InlineData("computers.scan", 0, "")]
    public void Stable_ids_provide_actionable_reasons(string id, int count, string reason) =>
        Assert.Equal(reason, MainActionAvailability.DisabledReason(id, count));
}
