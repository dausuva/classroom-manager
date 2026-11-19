// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>Matches the displayed command names, retaining the span to emphasize in each result.</summary>
public static class CommandSearch
{
    public sealed record Match(string Label, int Start, int Length);

    public static IReadOnlyList<Match> Find(IEnumerable<string> labels, string query)
    {
        query = query.Trim();
        if (query.Length < 2) return [];
        return labels.Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Select(label => new Match(label, label.IndexOf(query, StringComparison.OrdinalIgnoreCase), query.Length))
            .Where(match => match.Start >= 0)
            .OrderBy(match => match.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
