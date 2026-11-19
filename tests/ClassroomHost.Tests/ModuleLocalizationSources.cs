// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;

namespace ClassroomHost.Tests;

/// <summary>Presentation-only sources, not entire product families. Core execution/errors remain in their original files.</summary>
static class ModuleLocalizationSources
{
    public static readonly (string Domain, string File, string[] Extra)[] Modules =
    [
        ("dausuva.reporting.inventory", "ReportingPresentation.cs", []),
        ("dausuva.browser.inventory", "BrowserInventorySummary.cs", ["read", "unavailable", "failed"]),
        ("dausuva.automation.scheduling", "SchedulesWindow.cs", ["on", "off", "1st "]),
        ("dausuva.student-work.backup", "StudentBackupPresentation.cs", []),
        ("dausuva.software.inventory", "SoftwareInventoryPresentation.cs", ["unknown", "(unknown)", "{0} / {1} checked"]),
        ("dausuva.maintenance.inventory", "MaintenancePresentation.cs", ["{0} MB", "optional/driver", " · checked {0}"]),
    ];

    public static IReadOnlyList<string> Keys(string file, string[] extra) => StringExtractor.FromCSharp(
        File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", file)))
        .Where(StringExtractor.IsText).Where(text => file != "SchedulesWindow.cs" || text != "About this window")
        .Where(text => file != "SoftwareInventoryPresentation.cs" || text != ClassroomManager.Host.SoftwareInventoryPresentation.LegacyMissing)
        .Concat(extra).Select(text => text.Replace("\r", "")).Distinct(StringComparer.Ordinal).ToArray();
}
