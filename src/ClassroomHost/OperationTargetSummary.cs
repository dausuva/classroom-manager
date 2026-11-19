// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

public sealed record OperationTargetText(string Heading, string Detail);

public static class OperationTargetSummary
{
    public static OperationTargetText Format(IReadOnlyCollection<ClientInfo> targets, ClassroomManager.Shared.Translation? language = null)
    {
        if (targets.Count == 0)
            return new OperationTargetText(
                "Target: no computers selected",
                "Select computers below before choosing a classroom action.");

        var online = targets.Count(client => client.Level.Length > 0);
        var offline = targets.Count - online;
        var rooms = targets
            .Select(client => string.IsNullOrWhiteSpace(client.Room) ? "Unassigned" : client.Room.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(room => room, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var roomText = rooms.Count switch
        {
            0 => "Unassigned",
            1 => rooms[0],
            _ => Render($"{rooms.Count} rooms"),
        };
        if (language is not null && targets.All(client => string.IsNullOrWhiteSpace(client.Room)))
            roomText = language.Exact("Unassigned");

        return new OperationTargetText(
            // Two whole sentences rather than one with a word swapped in: each can be translated as it stands.
            targets.Count == 1
                ? Render($"Target: 1 selected computer{NamedTargets(targets)}")
                : Render($"Target: {targets.Count} selected computers{NamedTargets(targets)}"),
            Render($"Room: {roomText}  •  {online} online  •  {offline} offline"));

        string Render(FormattableString text) => language is null ? text.ToString() : language.Format(text);
    }

    const int NamedComputers = 5;

    /// <summary>Names the first selected computers in list order so the scope is recognizable at a glance.</summary>
    static string NamedTargets(IReadOnlyCollection<ClientInfo> targets)
    {
        var names = targets
            .Select(client => string.IsNullOrWhiteSpace(client.Name) ? client.Address.Trim() : client.Name.Trim())
            .Where(name => name.Length > 0)
            .ToList();
        if (names.Count == 0) return "";
        var named = string.Join(", ", names.Take(NamedComputers));
        return names.Count > NamedComputers ? $" — {named} + {names.Count - NamedComputers} more" : $" — {named}";
    }

    static string Plural(int count, string singular, string plural) => count == 1 ? singular : plural;
}
