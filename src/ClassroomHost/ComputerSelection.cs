// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// The rules of the selection buttons above the computer list (All, None, Invert, Online, Offline).
/// They act on the computers the list shows; one a filter hides keeps whatever it had.
/// </summary>
public static class ComputerSelection
{
    /// <summary>Answered the last check. "Online - nobody signed in" counts: the computer is on.</summary>
    public static bool IsOnline(ClientInfo computer) => computer.Status.StartsWith("Online", StringComparison.Ordinal);

    /// <summary>
    /// Did not answer the last check ("Offline (no reply)"). A computer that is only discovered and not yet
    /// checked, or one that is busy ("Checking...", "Done"), is neither online nor offline.
    /// </summary>
    public static bool IsOffline(ClientInfo computer) => computer.Status.StartsWith("Offline", StringComparison.Ordinal);

    /// <summary>Selects exactly the shown computers that match, and clears every other one.</summary>
    public static void Only(IEnumerable<ClientInfo> shown, IEnumerable<ClientInfo> all, Func<ClientInfo, bool> matches)
    {
        foreach (var computer in all) computer.IsSelected = false;
        foreach (var computer in shown.Where(matches)) computer.IsSelected = true;
    }

    /// <summary>The shown computers that were selected become unselected and the others selected.</summary>
    public static void Invert(IEnumerable<ClientInfo> shown)
    {
        foreach (var computer in shown.ToList()) computer.IsSelected = !computer.IsSelected;
    }
}
