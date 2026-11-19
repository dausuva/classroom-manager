// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

public sealed record AppBlockListSummary(
    IReadOnlyList<string> Union,
    int DistinctListCount,
    int AvailableCount,
    int UnavailableCount)
{
    public static AppBlockListSummary Create(IEnumerable<IReadOnlyList<string>> availableLists, int targetCount)
    {
        var normalized = availableLists.Select(list => list
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList())
            .ToList();
        var signatures = normalized.Select(list => string.Join("\0", list.Select(value => value.ToUpperInvariant())))
            .Distinct(StringComparer.Ordinal).Count();
        var union = normalized.SelectMany(list => list).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        return new AppBlockListSummary(union, signatures, normalized.Count, Math.Max(0, targetCount - normalized.Count));
    }
}
