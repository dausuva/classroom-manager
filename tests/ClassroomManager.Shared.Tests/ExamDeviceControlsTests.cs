// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class ExamDeviceControlsTests
{
    [Fact]
    public void Nothing_is_reported_when_every_control_changed()
    {
        Assert.Equal("", ExamDeviceControls.Summarize(
        [
            (ExamDeviceControls.AudioLabel, null),
            (ExamDeviceControls.CameraLabel, null),
            (ExamDeviceControls.UsbStorageLabel, null),
        ]));
        Assert.Equal("", ExamDeviceControls.Summarize([]));
    }

    [Fact]
    public void A_failed_control_is_named_alongside_the_ones_that_worked()
    {
        var summary = ExamDeviceControls.Summarize(
        [
            (ExamDeviceControls.AudioLabel, null),
            (ExamDeviceControls.CameraLabel, "Group Policy controls camera access on this computer."),
            (ExamDeviceControls.UsbStorageLabel, null),
        ]);

        Assert.Equal("Audio: done; Camera: Group Policy controls camera access on this computer.; USB storage: done", summary);
    }

    [Theory]
    [InlineData(false, false, false, "")]
    [InlineData(true, false, false, "audio muted")]
    [InlineData(false, true, true, "camera off, USB storage blocked")]
    [InlineData(true, true, true, "audio muted, camera off, USB storage blocked")]
    public void Selected_controls_are_described_for_confirmation_text(bool audio, bool camera, bool usb, string text)
    {
        Assert.Equal(text, ExamDeviceControls.Describe(audio, camera, usb));
    }

    [Fact]
    public void Device_controls_are_a_protocol_1_8_machine_wide_capability()
    {
        Assert.Equal("1.8", Protocol.RequiredVersion(Cmd.ExamDevicesSet));
        Assert.False(Cmd.IsUserOnly(Cmd.ExamDevicesSet));
        Assert.Contains(Cmd.ExamDevicesSet, Protocol.Capabilities);
        // A 1.7 Client knows Exam mode but not its device controls, so the Host must not assume them.
        Assert.True(Protocol.SupportsCommand(Cmd.ExamModeSet, "1.7", [Cmd.ExamModeSet], true));
        Assert.False(Protocol.SupportsCommand(Cmd.ExamDevicesSet, "1.7", [Cmd.ExamModeSet], true));
    }
}
