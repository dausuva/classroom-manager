// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

public sealed record QuickActionDefinition(string Id, string Label, string Icon, string CommandId);

public static class QuickActionCatalog
{
    public static readonly IReadOnlyList<QuickActionDefinition> All =
    [
        new("lock", "Lock screen", FluentIcons.Lock, "classroom.lock"),
        new("unlock", "Unlock screen", FluentIcons.Unlock, "classroom.unlock"),
        new("message", "Show message", FluentIcons.Message, "classroom.message"),
        new("exam", "Toggle Exam mode", FluentIcons.Page, "restrictions.exam"),
        new("send-files", "Send files...", FluentIcons.Folder, "classroom.send-files"),
        new("restart", "Restart in 2 minutes", FluentIcons.Refresh, "remote.power"),
        new("shutdown", "Shut down in 2 minutes", FluentIcons.Power, "remote.power"),
        new("cancel-power", "Cancel pending shutdown", FluentIcons.Cancel, "remote.cancel-power"),
    ];

    public static List<string> Normalize(IEnumerable<string>? ids)
    {
        var allowed = All.Select(action => action.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (ids ?? ["lock", "unlock", "send-files"])
            .Where(id => allowed.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
