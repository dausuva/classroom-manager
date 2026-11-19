// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>
/// The wire words of the Appearance catalogue commands (<see cref="Cmd.AppearanceCatalog"/>, <see cref="Cmd.AppearanceGet"/>,
/// <see cref="Cmd.AppearanceSet"/>, <see cref="Cmd.AppearanceReset"/>): the protocol that first has them, the kinds and effects a setting
/// can have, and the result of one setting in a reply. The Client builds the catalogue (it knows the Windows build and has the handlers);
/// the Host's Appearance module reads it.
/// </summary>
public static class AppearanceWire
{
    /// <summary>The protocol from which a Client answers the four Appearance commands.</summary>
    public const string RequiredVersion = "1.21";

    // Kinds of setting: how the Host draws the control.
    public const string KindToggle = "toggle";
    public const string KindChoice = "choice";
    public const string KindNumber = "number";
    public const string KindColour = "colour";

    // How long until a change shows: what the Host tells the person.
    public const string ApplyNow = "now";
    public const string ApplyExplorer = "explorer";
    public const string ApplySignIn = "sign-in";

    // Whether a change can strand the person at the computer: "confirm" asks the Host to ask twice (and reverts by itself when not confirmed).
    public const string RiskReversible = "reversible";
    public const string RiskConfirm = "confirm";

    // Result of one setting in an appearance.set or appearance.reset reply.
    public const string Applied = "applied";
    public const string Unchanged = "unchanged";
    public const string Unsupported = "unsupported";
    public const string Failed = "failed";
    public const string Restored = "restored";

    /// <summary>The most settings one request may name.</summary>
    public const int MaxSettingsPerRequest = 64;
}
