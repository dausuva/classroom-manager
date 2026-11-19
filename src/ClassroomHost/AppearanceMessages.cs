// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>Exact keys of the Appearance presentation domain, including short/data-bearing messages missed by the legacy heuristic.</summary>
public static class AppearanceMessages
{
    public const string TurnOff = "None — turn the screen saver off";
    public const string InstallFile = "Install a .scr file from this computer";
    public const string RestorePrevious = "Restore each user's previous screen saver";
    public const string Reading = "Reading screen saver settings from {0} computer(s)...";
    public const string AvailableChoice = "{0} — already on every selected computer that answered";
    public const string NoSettings = "No selected computer reported its screen saver settings. A user must be signed in for this action.";
    public const string CurrentSetting = "Current setting on {0} of {1} selected computer(s): {2}.";
    public const string PolicyManaged = "A Group Policy controls the screen saver on {0} computer(s); the change will be refused there.";
    public const string Unavailable = "Unavailable:\n";
    public const string ChoiceLabel = "_Screen saver";
    public const string FileLabel = "Screen saver _file (only for “Install a .scr file”)";
    public const string MinutesLabel = "Start after this many idle _minutes";
    public const string SecureLabel = "On resume, show the sign-in screen";
    public const string DisplayLabel = "While plugged in, turn the screen _off after the screen saver has run this many minutes (0 = never, empty = leave Windows' setting)";
    public const string Usage = "{0} on {1}";
    public const string Off = "off";
}
