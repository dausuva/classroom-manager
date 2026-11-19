// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;

namespace ClassroomManager.Shared;

/// <summary>
/// The answer to <see cref="Cmd.DisplayWake"/>, which stops a running screen saver and turns the
/// display back on for the signed-in user. It cannot sign anyone in: with "sign-in on resume" the
/// computer is left at the Windows lock screen, and the answer says so.
/// </summary>
public static class DisplayWakeRules
{
    public const string ScreenSaverWasRunning = "screenSaverWasRunning";
    public const string SignInScreen = "signInScreen";

    public static string Describe(JsonObject data) =>
        Describe(data[ScreenSaverWasRunning]?.GetValue<bool>() == true, data[SignInScreen]?.GetValue<bool>() == true);

    public static string Describe(bool screenSaverWasRunning, bool signInScreen) =>
        (screenSaverWasRunning ? "screen saver stopped" : "display woken, no screen saver was running") +
        (signInScreen ? "; Windows is showing its lock or sign-in screen, so someone has to sign in at the computer" : "");
}
