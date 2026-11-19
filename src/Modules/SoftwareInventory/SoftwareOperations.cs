// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Modules;

namespace ClassroomManager.SoftwareInventory;

/// <summary>
/// What the module asks of a computer about installed programs, through the run's ports only. The teacher chooses whether it is the
/// machine-wide list (administrator rights) or the signed-in user's own; the module declares both commands as able to do either.
/// The answers come back as the Client's JSON text; the program's key and view are the Client's own words, passed back unchanged.
/// </summary>
public sealed class SoftwareOperations(IModuleClient client)
{
    public const string List = "programs.list";
    public const string Uninstall = "programs.uninstall";

    public static IReadOnlyList<string> Commands { get; } = [List, Uninstall];

    // A list takes seconds; an uninstaller can run for a long time, and the person already waits for it.
    static readonly TimeSpan ListTime = TimeSpan.FromMinutes(5);
    static readonly TimeSpan UninstallTime = TimeSpan.FromMinutes(30);

    public Task<string> ListAsync(ModuleComputer computer, bool administrator, CancellationToken cancellation) =>
        Send(computer, administrator, List, "{}", ListTime, cancellation);

    /// <param name="key">The program's key as the Client listed it.</param>
    /// <param name="view">The registry view it was listed in, as the Client said.</param>
    /// <param name="arguments">Silent parameters typed by the teacher; empty to use the product's own trusted command.</param>
    public Task<string> UninstallAsync(ModuleComputer computer, bool administrator, string key, string view, string arguments, CancellationToken cancellation) =>
        Send(computer, administrator, Uninstall, new JsonObject { ["key"] = key, ["view"] = view, ["args"] = arguments }.ToJsonString(), UninstallTime, cancellation);

    Task<string> Send(ModuleComputer computer, bool administrator, string command, string arguments, TimeSpan timeout, CancellationToken cancellation) =>
        administrator ? client.CallAsSystemAsync(computer, command, arguments, cancellation, timeout) : client.CallAsync(computer, command, arguments, cancellation, timeout);
}
