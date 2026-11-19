// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.SoftwareInventory;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public static class SoftwareInventoryPresentation
{
    // Import/runtime compatibility only: the old second argument was an English plural suffix.
    public const string LegacyMissing = "Missing on {0} checked PC{1}";
    public static string Missing(int count)
    {
        const string key = "Missing on {0} checked PC(s)";
        var user = Loc.Current;
        if (user.IsOverride && !user.HasExact(key) && user.HasExact(LegacyMissing))
            return user.Format(System.Runtime.CompilerServices.FormattableStringFactory.Create(LegacyMissing, count, count == 1 ? "" : "s"));
        return ModuleLanguages.Software.FormatKey(key, count);
    }
    public const string AllPrograms = "All programs";
    public const string DifferencesOnly = "Differences only";
    public const string VersionDifferences = "Version differences";
    public const string MissingPrograms = "Missing on some PCs";
    public sealed record FilterChoice(string Value, string Shown);
    public static FilterChoice[] Filters => new[] { AllPrograms, DifferencesOnly, VersionDifferences, MissingPrograms }
        .Select(value => new FilterChoice(value, ModuleLanguages.Software.Text(value))).ToArray();

    public static ComparedProgram BuildOne(SoftwareComparison report, IReadOnlyList<ComputerProgramEntry> entries)
    {
        var language = ModuleLanguages.Software;
        var missing = report.Checked - report.Installed;
        var versions = report.Versions;
        var computerVersions = report.Computers.Select(computer =>
        {
            var value = string.Join(" / ", computer.Versions);
            return $"{computer.Name}: {(value.Length == 0 ? language.Text("unknown") : value)}";
        });
        var notes = new List<string>();
        if (versions.Count > 1) notes.Add(language.Text("Versions differ"));
        if (missing > 0) notes.Add(Missing(missing));
        if (report.Unavailable > 0) notes.Add(language.Format($"{report.Unavailable} unavailable"));
        if (notes.Count == 0) notes.Add(language.Text("Same version on all checked PCs"));
        return new ComparedProgram
        {
            Name = report.Name,
            Versions = versions.Count == 0 ? language.Text("(unknown)") : string.Join(", ", versions),
            Publishers = report.Publishers.Count == 0 ? language.Text("(unknown)") : string.Join(", ", report.Publishers),
            Installed = language.Format($"{report.Installed} / {report.Checked} checked"),
            Silent = $"{report.Silent} / {report.EntryTokens.Count}",
            Computers = string.Join("; ", computerVersions),
            MissingComputers = report.MissingComputers.Count == 0 ? "—" : string.Join(", ", report.MissingComputers),
            Comparison = string.Join(" · ", notes),
            HasVersionDifference = versions.Count > 1,
            IsMissingFromSome = missing > 0,
            HasDifference = versions.Count > 1 || missing > 0,
            Entries = report.EntryTokens.Select(token => entries[token]).ToArray(),
        };
    }
}
