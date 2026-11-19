// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.SoftwareInventory;

public sealed record SoftwareTarget(int Id, string Name);
public sealed record SoftwareProgram(int Token, string Name, string Version, string Publisher, bool Silent);
public sealed record SoftwareSnapshot(int TargetId, IReadOnlyList<SoftwareProgram> Programs);
public sealed record TargetVersions(string Name, IReadOnlyList<string> Versions);
public sealed record SoftwareComparison(string Name, IReadOnlyList<string> Versions, IReadOnlyList<string> Publishers,
    int Installed, int Checked, int Unavailable, int Silent, IReadOnlyList<TargetVersions> Computers,
    IReadOnlyList<string> MissingComputers, IReadOnlyList<int> EntryTokens);

/// <summary>Pure comparison of approved inventory fields; tokens are opaque, never executable identities.</summary>
public static class SoftwareInventoryModule
{
    public const string Id = "dausuva.software.inventory";
    public const int ContractVersion = 1;

    public static IReadOnlyList<SoftwareComparison> Compare(IReadOnlyList<SoftwareTarget> selected, IReadOnlyList<SoftwareSnapshot> snapshots)
    {
        var targets = selected.ToDictionary(target => target.Id);
        var checkedTargets = snapshots.ToDictionary(snapshot => snapshot.TargetId);
        if (checkedTargets.Keys.Any(id => !targets.ContainsKey(id))) throw new ArgumentException(nameof(snapshots));
        var entries = snapshots.SelectMany(snapshot => snapshot.Programs.Select(program => (snapshot.TargetId, Program: program))).ToArray();
        if (entries.Select(entry => entry.Program.Token).Distinct().Count() != entries.Length) throw new ArgumentException(nameof(snapshots));
        return Freeze(entries.GroupBy(entry => entry.Program.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                var installed = group.Select(entry => entry.TargetId).ToHashSet();
                return new SoftwareComparison(group.Key, Values(group.Select(entry => entry.Program.Version)),
                    Values(group.Select(entry => entry.Program.Publisher)), installed.Count, checkedTargets.Count,
                    targets.Count - checkedTargets.Count, group.Count(entry => entry.Program.Silent),
                    Freeze(group.GroupBy(entry => entry.TargetId).OrderBy(computer => targets[computer.Key].Name, StringComparer.OrdinalIgnoreCase)
                        .Select(computer => new TargetVersions(targets[computer.Key].Name, Values(computer.Select(entry => entry.Program.Version))))),
                    Freeze(checkedTargets.Keys.Except(installed).Select(id => targets[id].Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)),
                    Freeze(group.Select(entry => entry.Program.Token)));
            }));
    }

    static IReadOnlyList<string> Values(IEnumerable<string> values) => Freeze(values.Select(value => value.Trim())
        .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
    static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}
