// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>
/// Rules for the "turn off the display" time of a plugged-in computer (<see cref="Cmd.PowerDisplaySet"/>).
/// It belongs with the screen saver: Windows counts both from the same moment of idleness, so a
/// display time shorter than the screen saver's wait means the screen saver is never seen.
/// </summary>
public static class DisplayPowerRules
{
    public const string Seconds = "acSec";
    public const string Restore = "restore";

    public const int MinSeconds = 60;
    public const int MaxSeconds = 24 * 60 * 60;
    public const int MaxMinutesShown = 12 * 60;

    /// <summary>0 means the display is never turned off; anything else is kept within one minute and one day.</summary>
    public static int Clamp(int seconds) => seconds <= 0 ? 0 : Math.Clamp(seconds, MinSeconds, MaxSeconds);

    /// <summary>
    /// The idle time after which the display goes dark when the screen saver starts after
    /// <paramref name="screenSaverWaitSeconds"/> and should then be shown for <paramref name="minutesShown"/>;
    /// 0 minutes means it is shown until someone uses the computer.
    /// </summary>
    public static int AfterScreenSaver(int screenSaverWaitSeconds, int minutesShown) =>
        minutesShown <= 0 ? 0 : Clamp(Math.Max(0, screenSaverWaitSeconds) + minutesShown * 60);

    /// <summary>Reads the Host's field: empty leaves the Windows setting alone, 0 is "never", otherwise whole minutes.</summary>
    public static bool TryParseMinutesShown(string text, out int? minutes)
    {
        minutes = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(text.Trim(), out var value) || value < 0 || value > MaxMinutesShown) return false;
        minutes = value;
        return true;
    }

    public static string Describe(int seconds) =>
        seconds <= 0 ? "never" : seconds % 60 == 0 ? $"after {seconds / 60} idle minute(s)" : $"after {seconds} idle second(s)";
}
