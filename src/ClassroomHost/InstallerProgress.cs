// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

public static class InstallerProgress
{
    public static string RunningStatus(string fileName, TimeSpan elapsed) =>
        $"Installing {fileName} — running {FormatElapsed(elapsed)} (installer does not report percent complete)";

    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
}
