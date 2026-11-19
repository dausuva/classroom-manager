// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

public sealed record ComputerProcessEntry(ClientInfo Client, ProcessEntry Process);

public sealed class ComparedProcess
{
    public string Name { get; init; } = "";
    public string Versions { get; init; } = "";
    public string Running { get; init; } = "";
    public int Instances { get; init; }
    public string Memory { get; init; } = "";
    public string Owners { get; init; } = "";
    public string Types { get; init; } = "";
    public string ComputerDetails { get; init; } = "";
    public string MissingComputers { get; init; } = "";
    public string WindowTitles { get; init; } = "";
    public string Paths { get; init; } = "";
    public string Comparison { get; init; } = "";
    public bool HasDifference { get; init; }
    public bool HasVersionDifference { get; init; }
    public bool IsMissingFromSome { get; init; }
    public bool HasVisibleWindow { get; init; }
    public IReadOnlyList<ComputerProcessEntry> Entries { get; init; } = [];
}

public static class ProcessInventoryComparison
{
    public static List<ComparedProcess> Build(
        IReadOnlyList<ClientInfo> allClients,
        IReadOnlyDictionary<ClientInfo, IReadOnlyList<ProcessEntry>> successfulInventories)
    {
        var checkedClients = successfulInventories.Keys.ToHashSet();
        var unavailable = allClients.Count - checkedClients.Count;
        return successfulInventories
            .SelectMany(pair => pair.Value.Select(process => new ComputerProcessEntry(pair.Key, process)))
            .GroupBy(entry => entry.Process.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildOne(group.Key, group.ToList(), checkedClients, unavailable))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool SafeToBatchEnd(ProcessEntry process) =>
        process.Pid > 4 && process.StartUtcTicks > 0 &&
        (process.Role.Equals("User", StringComparison.OrdinalIgnoreCase) ||
         process.Role.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) &&
        !process.Name.Equals("ClassroomClient", StringComparison.OrdinalIgnoreCase) &&
        !process.Name.Equals("ClassroomHost", StringComparison.OrdinalIgnoreCase);

    static ComparedProcess BuildOne(
        string name,
        List<ComputerProcessEntry> entries,
        HashSet<ClientInfo> checkedClients,
        int unavailable)
    {
        var runningClients = entries.Select(entry => entry.Client).ToHashSet();
        var missingClients = checkedClients.Except(runningClients)
            .OrderBy(client => client.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var versions = Distinct(entries.Select(entry => entry.Process.Version));
        var paths = Distinct(entries.Select(entry => entry.Process.Path));
        var owners = Distinct(entries.Select(entry => entry.Process.Owner));
        var types = Distinct(entries.Select(entry => entry.Process.Kind));
        var titles = Distinct(entries.Select(entry => entry.Process.Title));
        var instanceCounts = entries.GroupBy(entry => entry.Client).Select(group => group.Count()).Distinct().ToList();
        var notes = new List<string>();
        if (versions.Count > 1) notes.Add("Versions differ");
        if (paths.Count > 1) notes.Add("Executable paths differ");
        if (instanceCounts.Count > 1) notes.Add("Instance counts differ");
        if (missingClients.Count > 0) notes.Add($"Not running on {missingClients.Count} checked PC{(missingClients.Count == 1 ? "" : "s")}");
        if (unavailable > 0) notes.Add($"{unavailable} unavailable");
        if (notes.Count == 0) notes.Add("Same on all checked PCs");

        var computerDetails = entries.GroupBy(entry => entry.Client)
            .OrderBy(group => group.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var groupVersions = Distinct(group.Select(entry => entry.Process.Version));
                var version = groupVersions.Count == 0 ? "version unknown" : "v" + string.Join(" / ", groupVersions);
                var memory = group.Sum(entry => entry.Process.MemoryMb);
                var windows = group.Count(entry => entry.Process.Title.Length > 0);
                return $"{group.Key.Name}: {group.Count()}×, {version}, {memory:0.0} MB" +
                       (windows > 0 ? $", {windows} windowed" : "");
            });

        return new ComparedProcess
        {
            Name = name,
            Versions = versions.Count == 0 ? "(unknown)" : string.Join(", ", versions),
            Running = $"{runningClients.Count} / {checkedClients.Count} checked",
            Instances = entries.Count,
            Memory = $"{entries.Sum(entry => entry.Process.MemoryMb):0.0} MB",
            Owners = owners.Count == 0 ? "(unknown)" : string.Join(", ", owners),
            Types = types.Count == 0 ? "(unknown)" : string.Join(", ", types),
            ComputerDetails = string.Join("; ", computerDetails),
            MissingComputers = missingClients.Count == 0 ? "—" : string.Join(", ", missingClients.Select(client => client.Name)),
            WindowTitles = titles.Count == 0 ? "—" : string.Join(" | ", titles),
            Paths = paths.Count == 0 ? "(unavailable)" : string.Join("; ", paths),
            Comparison = string.Join(" · ", notes),
            HasVersionDifference = versions.Count > 1,
            IsMissingFromSome = missingClients.Count > 0,
            HasVisibleWindow = titles.Count > 0,
            HasDifference = versions.Count > 1 || paths.Count > 1 || instanceCounts.Count > 1 || missingClients.Count > 0,
            Entries = entries,
        };
    }

    static List<string> Distinct(IEnumerable<string> values) => values
        .Select(value => value.Trim())
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToList();
}
