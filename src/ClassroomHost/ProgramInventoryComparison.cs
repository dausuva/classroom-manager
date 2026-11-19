// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.SoftwareInventory;

namespace ClassroomManager.Host;

public sealed record ComputerProgramEntry(ClientInfo Client, ProgramEntry Program);

public sealed class ComparedProgram
{
    public string Name { get; init; } = "";
    public string Versions { get; init; } = "";
    public string Publishers { get; init; } = "";
    public string Installed { get; init; } = "";
    public string Silent { get; init; } = "";
    public string Computers { get; init; } = "";
    public string MissingComputers { get; init; } = "";
    public string Comparison { get; init; } = "";
    public bool HasDifference { get; init; }
    public bool HasVersionDifference { get; init; }
    public bool IsMissingFromSome { get; init; }
    public IReadOnlyList<ComputerProgramEntry> Entries { get; init; } = [];
}

/// <summary>Wording for a batch uninstall as it appears in the Operations center and activity log.</summary>
public static class ProgramUninstallOutcome
{
    const int NamedProducts = 3;

    public static string Title(IReadOnlyList<string> productNames)
    {
        var named = string.Join(", ", productNames.Take(NamedProducts));
        return productNames.Count > NamedProducts
            ? $"Uninstall {named} + {productNames.Count - NamedProducts} more"
            : $"Uninstall {named}";
    }

    /// <summary>Returns an empty string when every uninstall on the computer succeeded.</summary>
    public static string FailureMessage(int attempted, IReadOnlyList<string> failures) =>
        failures.Count == 0
            ? ""
            : $"{failures.Count} of {attempted} uninstall(s) failed — {string.Join("; ", failures)}";
}

public static class ProgramInventoryComparison
{
    public static List<ComparedProgram> Build(
        IReadOnlyList<ClientInfo> allClients,
        IReadOnlyDictionary<ClientInfo, IReadOnlyList<ProgramEntry>> successfulInventories)
    {
        var targets = allClients.Select((client, id) => (client, id)).ToDictionary(item => item.client, item => item.id);
        var entries = new List<ComputerProgramEntry>();
        var snapshots = successfulInventories.Select(pair => new SoftwareSnapshot(targets[pair.Key], pair.Value.Select(program =>
        {
            var token = entries.Count;
            entries.Add(new(pair.Key, program));
            return new SoftwareProgram(token, program.Name, program.Version, program.Publisher, program.Silent);
        }).ToArray())).ToArray();
        return SoftwareInventoryModule.Compare(allClients.Select(client => new SoftwareTarget(targets[client], client.Name)).ToArray(), snapshots)
            .Select(report => SoftwareInventoryPresentation.BuildOne(report, entries)).ToList();
    }

}
