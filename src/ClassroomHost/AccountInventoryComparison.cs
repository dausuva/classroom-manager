// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

public sealed record ComputerAccountEntry(ClientInfo Client, AccountEntry Account);

public sealed class ComparedAccount
{
    public string Name { get; init; } = "";
    public string DisplayNames { get; init; } = "";
    public string Present { get; init; } = "";
    public string Enabled { get; init; } = "";
    public string Administrators { get; init; } = "";
    public string Locked { get; init; } = "";
    public string BuiltIn { get; init; } = "";
    public string Computers { get; init; } = "";
    public string MissingComputers { get; init; } = "";
    public string Comparison { get; init; } = "";
    public bool HasDifferenceOrAttention { get; init; }
    public IReadOnlyList<ComputerAccountEntry> Entries { get; init; } = [];
}

public static class AccountInventoryComparison
{
    public static List<ComparedAccount> Build(
        IReadOnlyList<ClientInfo> allClients,
        IReadOnlyDictionary<ClientInfo, IReadOnlyList<AccountEntry>> successfulInventories)
    {
        var checkedClients = successfulInventories.Keys.ToHashSet();
        var unavailable = allClients.Count - checkedClients.Count;
        var entries = successfulInventories.SelectMany(pair => pair.Value
            .Where(account => account.Name.Trim().Length > 0)
            .Select(account => new ComputerAccountEntry(pair.Key, account)));

        return entries
            .GroupBy(entry => entry.Account.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildOne(group.Key, group.ToList(), checkedClients, unavailable))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static ComparedAccount BuildOne(
        string name,
        List<ComputerAccountEntry> entries,
        HashSet<ClientInfo> checkedClients,
        int unavailable)
    {
        var presentClients = entries.Select(entry => entry.Client).ToHashSet();
        var missingClients = checkedClients.Except(presentClients)
            .Select(client => client.Name)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var displayNames = entries.Select(entry => entry.Account.DisplayName.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var enabledStates = entries.Select(entry => entry.Account.Enabled).Distinct().ToList();
        var adminStates = entries.Select(entry => entry.Account.Admin).Distinct().ToList();
        var builtInStates = entries.Select(entry => entry.Account.BuiltIn).Distinct().ToList();
        var locked = entries.Count(entry => entry.Account.Locked);
        var notes = new List<string>();
        if (missingClients.Count > 0)
            notes.Add($"Missing on {missingClients.Count} checked PC{(missingClients.Count == 1 ? "" : "s")}");
        if (enabledStates.Count > 1) notes.Add("Enabled state differs");
        if (adminStates.Count > 1) notes.Add("Administrator membership differs");
        if (builtInStates.Count > 1) notes.Add("Built-in classification differs");
        if (displayNames.Count > 1) notes.Add("Display name differs");
        if (locked > 0) notes.Add($"Locked on {locked} PC{(locked == 1 ? "" : "s")}");
        if (unavailable > 0) notes.Add($"{unavailable} unavailable");
        if (notes.Count == 0) notes.Add("Same on all checked PCs");

        var computers = entries
            .OrderBy(entry => entry.Client.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Client.Name}: " +
                $"{(entry.Account.Enabled ? "enabled" : "disabled")}, " +
                $"{(entry.Account.Admin ? "admin" : "standard")}" +
                (entry.Account.Locked ? ", locked" : ""))
            .ToList();

        return new ComparedAccount
        {
            Name = entries[0].Account.Name,
            DisplayNames = displayNames.Count == 0 ? "—" : string.Join(", ", displayNames),
            Present = $"{presentClients.Count} / {checkedClients.Count} checked",
            Enabled = $"{entries.Count(entry => entry.Account.Enabled)} / {entries.Count}",
            Administrators = $"{entries.Count(entry => entry.Account.Admin)} / {entries.Count}",
            Locked = locked == 0 ? "none" : $"{locked} / {entries.Count}",
            BuiltIn = builtInStates.Count > 1 ? "mixed" : builtInStates[0] ? "yes" : "no",
            Computers = string.Join("; ", computers),
            MissingComputers = missingClients.Count == 0 ? "—" : string.Join(", ", missingClients),
            Comparison = string.Join(" · ", notes),
            HasDifferenceOrAttention = missingClients.Count > 0 || enabledStates.Count > 1 ||
                adminStates.Count > 1 || builtInStates.Count > 1 || displayNames.Count > 1 || locked > 0,
            Entries = entries,
        };
    }
}
