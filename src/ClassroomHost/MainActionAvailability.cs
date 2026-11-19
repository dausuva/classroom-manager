// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

public enum ActionTargetRequirement
{
    None,
    AnySelection,
    SingleSelection,
}

public static class MainActionAvailability
{
    static readonly HashSet<string> SingleSelection = new(StringComparer.OrdinalIgnoreCase)
    {
        "remote.terminal", "remote.registry", "classroom.managed-wallpapers", "classroom.themes", "remote.uninstall-client",
    };

    static readonly HashSet<string> AnySelection = TeacherCommandCatalog.Sends.Keys
        .Except(["computers.refresh", "remote.install", "restrictions.exam", "classroom.transfer-history"], StringComparer.OrdinalIgnoreCase)
        .Concat(["computers.remove", "computers.wake", "computers.assign-room"])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Labels are accepted for older callers; production controls carry a stable catalog ID.
    public static ActionTargetRequirement Requirement(string command)
    {
        if (command.StartsWith("custom:", StringComparison.Ordinal)) return ActionTargetRequirement.AnySelection;
        var id = TeacherCommandCatalog.All.FirstOrDefault(item => item.Label.Equals(command, StringComparison.OrdinalIgnoreCase))?.Id ?? command;
        return SingleSelection.Contains(id) ? ActionTargetRequirement.SingleSelection
            : AnySelection.Contains(id) ? ActionTargetRequirement.AnySelection : ActionTargetRequirement.None;
    }

    public static string DisabledReason(string command, int selectedCount) => CanExecute(command, selectedCount) ? "" : Requirement(command) switch
    {
        ActionTargetRequirement.SingleSelection => "Select exactly one computer.",
        ActionTargetRequirement.AnySelection => "Select at least one computer.",
        _ => "",
    };

    public static bool CanExecute(string label, int selectedCount) => Requirement(label) switch
    {
        ActionTargetRequirement.AnySelection => selectedCount > 0,
        ActionTargetRequirement.SingleSelection => selectedCount == 1,
        _ => true,
    };
}
