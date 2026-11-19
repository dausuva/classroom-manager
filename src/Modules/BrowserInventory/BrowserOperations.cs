// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Modules;

namespace ClassroomManager.BrowserInventory;

/// <summary>What the module reads from a computer about browsers, through the run's ports only. Nothing here changes a browser.</summary>
public sealed class BrowserOperations(IModuleClient client)
{
    public const string Policy = "browser.policy.get";
    public const string Browsers = "browser.list";
    public const string Profiles = "browser.profiles.list";

    public static IReadOnlyList<string> Commands { get; } = [Policy, Browsers, Profiles];

    /// <summary>The commands that are read with administrator rights when the machine-wide state is asked for.</summary>
    public static IReadOnlyList<string> SystemCommands { get; } = [Policy, Browsers];

    /// <summary>Reads one of <see cref="Commands"/>; the broker refuses anything else, and administrator rights for a command not in <see cref="SystemCommands"/>.</summary>
    public Task<string> ReadAsync(ModuleComputer computer, bool administrator, string command, CancellationToken cancellation) =>
        administrator ? client.CallAsSystemAsync(computer, command, "{}", cancellation) : client.CallAsync(computer, command, "{}", cancellation);
}
