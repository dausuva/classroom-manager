// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using Microsoft.Win32;

namespace ClassroomManager.Client;

/// <summary>
/// The registry views to read when listing what is installed or what starts with Windows. 64-bit Windows has two (the 64-bit one and
/// the one 32-bit programs see); 32-bit Windows has one, and asking it for both lists every entry twice.
/// </summary>
static class RegistryViews
{
    public static RegistryView[] ForWindows(bool is64BitWindows) =>
        is64BitWindows ? [RegistryView.Registry64, RegistryView.Registry32] : [RegistryView.Registry64];

    public static RegistryView[] All => ForWindows(Environment.Is64BitOperatingSystem);
}
