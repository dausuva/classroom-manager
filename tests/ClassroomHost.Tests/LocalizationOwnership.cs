// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Appearance;
using ClassroomManager.Automation;
using ClassroomManager.BrowserInventory;
using ClassroomManager.Maintenance;
using ClassroomManager.Reporting;
using ClassroomManager.SoftwareInventory;
using ClassroomManager.StudentWork;

namespace ClassroomHost.Tests;

/// <summary>L10N-1 review ledger, not a runtime registry or permission to remove legacy entries.</summary>
static class LocalizationOwnership
{
    public const string Core = "core";
    public const string InvariantExport = "invariant-export";
    public sealed record Area(string Domain, string File, string? Start = null, string? End = null);
    public sealed record Use(string Domain, string File);
    public sealed record Entry(string Text, IReadOnlyList<Use> Uses)
    {
        public IReadOnlyList<string> Domains => Uses.Select(use => use.Domain).Distinct().Order().ToArray();
        public bool RetainInCore => Domains.Contains(Core);
    }

    public static readonly (string Folder, string Domain)[] Modules =
    [
        ("Reporting", InventoryReportingModule.Id), ("BrowserInventory", BrowserInventoryModule.Id),
        ("Automation", SchedulingModule.Id), ("StudentWork", StudentBackupModule.Id),
        ("Appearance", AppearanceInventoryModule.Id), ("SoftwareInventory", SoftwareInventoryModule.Id),
        ("Maintenance", MaintenanceInventoryModule.Id),
    ];

    // Scope is the currently extracted feature's presentation, not its entire future product family.
    // Unique anchors deliberately fail on source drift; no historical line numbers or guessed method parser.
    public static readonly Area[] Areas =
    [
        new(InventoryReportingModule.Id, "ClassroomHost/ReportingPresentation.cs"),
        new(BrowserInventoryModule.Id, "ClassroomHost/BrowserInventorySummary.cs"),
        new(SchedulingModule.Id, "ClassroomHost/SchedulesWindow.cs"),
        new(StudentBackupModule.Id, "ClassroomHost/StudentBackupPresentation.cs"),
        // The backup's failure messages are shown by the Host in its own log, so they stay the Host's texts although the module now throws them.
        new(Core, "Modules/StudentWork/StudentBackupFlow.cs"),
        new(AppearanceInventoryModule.Id, "ClassroomHost/ScreenSaverInventory.cs", "public static class ScreenSaverInventory", "public sealed record ScreenSaverFile"),
        new(AppearanceInventoryModule.Id, "ClassroomHost/AppearanceMessages.cs"),
        new(AppearanceInventoryModule.Id, "ClassroomHost/AppearancePresentation.cs"),
        new(SoftwareInventoryModule.Id, "ClassroomHost/SoftwareInventoryPresentation.cs"),
        new(MaintenanceInventoryModule.Id, "ClassroomHost/MaintenancePresentation.cs"),
        new(MaintenanceInventoryModule.Id, "ClassroomHost/BulkWindowsUpdateWindow.cs", "sealed class BulkUpdateItem", "public sealed class BulkWindowsUpdateWindow"),
        new(MaintenanceInventoryModule.Id, "ClassroomHost/BulkWindowsUpdateWindow.cs", "    void SetPauseStatus(", "    async Task RunOneAsync("),
    ];

    // This warning is in Appearance's read/form region, but must remain available to trusted core.
    public const string ScreenSaverTrustWarning = "The setting applies to the user signed in on each computer. A screen saver is a program that runs in that user's session, so install only files you built or trust.";

    public static IReadOnlyList<Entry> Build()
    {
        // PoFile.Write normalizes CRLF/lone CR; compare actual catalog identities, not raw source spelling.
        var extracted = StringExtractor.ExtractAll().GroupBy(entry => entry.Text.Replace("\r", ""), StringComparer.Ordinal)
            .Select(group => (Text: group.Key, Files: group.SelectMany(entry => entry.Files).Distinct().ToList())).ToList();
        var root = Path.Combine(StringExtractor.RepositoryRoot(), "src");
        var uses = extracted.ToDictionary(entry => entry.Text, _ => new HashSet<Use>(), StringComparer.Ordinal);
        foreach (var file in extracted.SelectMany(entry => entry.Files).Distinct())
        {
            var source = File.ReadAllText(Path.Combine(root, file));
            var original = source;
            var known = extracted.Where(entry => entry.Files.Contains(file)).Select(entry => entry.Text).ToHashSet(StringComparer.Ordinal);
            var ranges = new List<(int Start, int End)>();
            foreach (var area in Areas.Where(area => area.File == file))
            {
                var start = area.Start is null ? 0 : Anchor(original, area.Start);
                var end = area.End is null ? original.Length : Anchor(original, area.End);
                if (end <= start || ranges.Any(range => start < range.End && end > range.Start)) throw new InvalidDataException(file);
                ranges.Add((start, end));
                Include(original[start..end], area.Domain);
            }
            foreach (var range in ranges.OrderByDescending(range => range.Start))
                source = source.Remove(range.Start, range.End - range.Start).Insert(range.Start, " ");
            var module = Modules.FirstOrDefault(module => file.StartsWith("Modules/" + module.Folder + "/", StringComparison.Ordinal));
            // The reporting assembly emits stable CSV schema/data, not localized presentation.
            Include(source, module.Folder == "Reporting" ? InvariantExport : module.Domain ?? Core);
            if (known.Contains(ScreenSaverTrustWarning)) uses[ScreenSaverTrustWarning].Add(new(Core, file));
            foreach (var presentation in ModuleLocalizationSources.Modules.Where(presentation => file == "ClassroomHost/" + presentation.File))
                foreach (var key in presentation.Extra.Where(known.Contains)) uses[key].Add(new(presentation.Domain, file));
            foreach (var text in known)
                if (!uses[text].Any(use => use.File == file)) throw new InvalidDataException(file + ": " + text);

            void Include(string fragment, string domain)
            {
                var texts = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                    ? StringExtractor.FromXaml(fragment) : StringExtractor.FromCSharp(fragment, file.StartsWith("ClassroomClient/", StringComparison.Ordinal));
                foreach (var text in texts.Select(text => text.Replace("\r", "")).Where(known.Contains)) uses[text].Add(new(domain, file));
            }
        }
        // Validate even zero-text sources/areas, so a removed file cannot silently disappear from the map.
        foreach (var area in Areas)
        {
            var source = File.ReadAllText(Path.Combine(root, area.File));
            if (area.Start is not null) Anchor(source, area.Start);
            if (area.End is not null) Anchor(source, area.End);
        }
        return extracted.Select(entry => new Entry(entry.Text, uses[entry.Text].OrderBy(use => use.Domain).ThenBy(use => use.File).ToArray())).ToArray();
    }

    static int Anchor(string source, string anchor)
    {
        var found = source.IndexOf(anchor, StringComparison.Ordinal);
        if (found < 0 || source.IndexOf(anchor, found + anchor.Length, StringComparison.Ordinal) >= 0) throw new InvalidDataException(anchor);
        return found;
    }
}
