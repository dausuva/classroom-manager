// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Appearance;

public sealed record ScreenSaverSnapshot(bool Active, string Name, bool PolicyManaged, IReadOnlyList<string> Available);
public sealed record AppearanceCount(string Name, int Count);
public sealed record ScreenSaverReport(int Read, int PolicyManaged, IReadOnlyList<string> CommonNames, IReadOnlyList<AppearanceCount> Usage);

/// <summary>Read-only projection of approved user-environment snapshots. No changes, transport, paths or rollback state.</summary>
public static class AppearanceInventoryModule
{
    public const string Id = "dausuva.appearance.inventory";
    public const int ContractVersion = 1;

    public static ScreenSaverReport Project(IReadOnlyList<ScreenSaverSnapshot> snapshots, string inactiveLabel)
    {
        var states = snapshots.Select(state => state with { Available = Array.AsReadOnly(state.Available.ToArray()) }).ToArray();
        var common = new HashSet<string>(states.FirstOrDefault()?.Available ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var state in states.Skip(1)) common.IntersectWith(state.Available);
        var usage = states.GroupBy(state => state.Active ? state.Name : inactiveLabel, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AppearanceCount(group.Key, group.Count())).ToArray();
        return new(states.Length, states.Count(state => state.PolicyManaged),
            Array.AsReadOnly(common.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()), Array.AsReadOnly(usage));
    }
}
