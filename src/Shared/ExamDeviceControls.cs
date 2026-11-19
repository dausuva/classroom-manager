// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>Argument names and result wording for the exam device controls (<see cref="Cmd.ExamDevicesSet"/>).</summary>
public static class ExamDeviceControls
{
    public const string MuteAudio = "muteAudio";
    public const string DisableCamera = "disableCamera";
    public const string BlockUsbStorage = "blockUsbStorage";

    public const string AudioLabel = "Audio";
    public const string CameraLabel = "Camera";
    public const string UsbStorageLabel = "USB storage";

    /// <summary>
    /// The three controls are independent, so one can fail while the others apply. Returns an empty
    /// string when every requested change worked; otherwise names each control with "done" or its error.
    /// </summary>
    public static string Summarize(IEnumerable<(string Control, string? Error)> outcomes)
    {
        var list = outcomes.ToList();
        return list.All(outcome => outcome.Error is null)
            ? ""
            : string.Join("; ", list.Select(outcome => $"{outcome.Control}: {outcome.Error ?? "done"}"));
    }

    /// <summary>For example "audio muted, camera off"; empty when no device control is selected.</summary>
    public static string Describe(bool muteAudio, bool disableCamera, bool blockUsbStorage)
    {
        var parts = new List<string>();
        if (muteAudio) parts.Add("audio muted");
        if (disableCamera) parts.Add("camera off");
        if (blockUsbStorage) parts.Add("USB storage blocked");
        return string.Join(", ", parts);
    }
}
