// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Modules;

/// <summary>The version of the contract between the Host and its modules. It rises only when a module written for the old one could break.</summary>
public static class ModuleContract
{
    public const int CurrentVersion = 1;
}

/// <summary>A scheduled action a module adds to the Host's list of actions, such as "backup". The Host shows it only while the module is on.</summary>
/// <param name="Code">Lower-case letters, digits and hyphens; stored in saved schedules, so it never changes once released.</param>
/// <param name="Label">The English text shown to the teacher.</param>
public sealed record ModuleAction(string Code, string Label);

/// <summary>What a module says about itself once its code is running. The Host compares it with the module's manifest.</summary>
/// <param name="Id">The module id; it must equal the id in <c>module.json</c>.</param>
/// <param name="Name">The name shown in Host settings.</param>
/// <param name="ContractVersion">The <see cref="ModuleContract.CurrentVersion"/> the module was built against.</param>
/// <param name="ClientCapabilities">The capabilities a Client must announce before the Host offers this module's features for it.</param>
public sealed record ModuleDescriptor(string Id, string Name, int ContractVersion, IReadOnlyList<string> ClientCapabilities)
{
    /// <summary>The Host services this module asks for. It is given those and nothing else; the manifest must list the same ones.</summary>
    public IReadOnlyList<ModulePortRequest> Ports { get; init; } = [];

    /// <summary>A small picture in plain ASCII, shown beside the module in Host settings. See <see cref="ModuleIcon"/> for the limits.</summary>
    public IReadOnlyList<string> Icon { get; init; } = [];

    /// <summary>
    /// The commands (a subset of <see cref="ClientCapabilities"/>) the module may send with administrator rights on the computer, through
    /// <see cref="IModuleClient.CallAsSystemAsync"/>. Any declared command may also be sent as the signed-in user, which is the lesser right.
    /// Settings says in plain words that the module may act with administrator rights.
    /// </summary>
    public IReadOnlyList<string> SystemCommands { get; init; } = [];

    /// <summary>The kinds of destination folder this module may copy files into (port <c>storage.sink</c>). The Host maps each to a folder the teacher chose.</summary>
    public IReadOnlyList<string> Sinks { get; init; } = [];

    /// <summary>The kinds of file this module may send to a computer (port <c>client.upload</c>); see <see cref="ModuleUploads"/>. Needs <c>storage.source</c> as well.</summary>
    public IReadOnlyList<string> Uploads { get; init; } = [];

    /// <summary>The scheduled actions this module adds to the Host.</summary>
    public IReadOnlyList<ModuleAction> ScheduledActions { get; init; } = [];
}

/// <summary>The rules for a module icon: ASCII art that every Windows font draws the same way, small enough for a settings page.</summary>
public static class ModuleIcon
{
    public const int MaxLines = 6;
    public const int MaxWidth = 16;

    /// <summary>Why the icon is not acceptable, in plain words. An empty list means it is, and no icon is acceptable too.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<string>? lines)
    {
        var problems = new List<string>();
        if (lines is null) return problems;
        if (lines.Count > MaxLines) problems.Add($"The icon has {lines.Count} lines; the limit is {MaxLines}.");
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line is null) { problems.Add($"Icon line {index + 1} is empty."); continue; }
            if (line.Length > MaxWidth) problems.Add($"Icon line {index + 1} is {line.Length} characters wide; the limit is {MaxWidth}.");
            if (line.Any(character => character < ' ' || character > '~')) problems.Add($"Icon line {index + 1} uses a character that is not plain ASCII.");
        }
        return problems;
    }
}

/// <summary>
/// The one type a module package must provide, named in the manifest's <c>entry</c>. It needs a public parameterless constructor and must not
/// do work there: the Host creates it only to read <see cref="Describe"/>.
/// </summary>
public interface IClassroomModule
{
    ModuleDescriptor Describe();

    /// <summary>Called once, after the module has been checked and accepted, with exactly the ports it asked for.</summary>
    void Attach(ModuleServices services) { }
}
