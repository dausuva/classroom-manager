// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Client;

/// <summary>
/// The Client installed as a Windows package (MSIX), for Intune and Configuration Manager (T-235; docs/windows/CLIENT-MSIX.md). A package cannot run
/// setup scripts, ask for the lab key, write firewall rules or update itself, so on a packaged Client:
/// the first start builds client.json from a kit file the administrator put in the data folder (<see cref="PackagedKit.KitFileName"/>), opens the three firewall
/// rules, and removes the kit file; Windows, not the Host, updates and removes the program; and the user helper is started by the package's startup task
/// instead of by the service (a program inside the package folder cannot be started by another process the usual way).
/// The Inno installer route is unchanged and remains the proven one; none of this runs unless Windows gives the process a package identity.
/// </summary>
static class PackagedClient
{
    /// <summary>Why a packaged Client does not update itself, or null for an installed (not packaged) one.</summary>
    public static string? SelfUpdateRefusal(bool packaged) => packaged
        ? "This Client was installed as a Windows package (MSIX), so Windows updates it: deploy the new package with Intune or Configuration Manager. It does not replace itself."
        : null;

    /// <summary>Why a packaged Client does not remove itself, or null for an installed one.</summary>
    public static string? UninstallRefusal(bool packaged) => packaged
        ? "This Client was installed as a Windows package (MSIX). Remove the package with Intune or Configuration Manager, or in Settings > Apps."
        : null;

    /// <summary>The service starts missing user helpers itself only when it is not packaged; a packaged helper is started by the package's startup task.</summary>
    public static bool ServiceStartsHelpers(bool packaged) => !packaged;
}
