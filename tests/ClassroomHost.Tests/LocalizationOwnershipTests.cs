// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LocalizationOwnershipTests(ITestOutputHelper output)
{
    static readonly Lazy<IReadOnlyList<LocalizationOwnership.Entry>> Inventory = new(LocalizationOwnership.Build);

    [Fact]
    public void Ledger_covers_every_current_extracted_key_and_reports_shared_consumers()
    {
        var inventory = Inventory.Value;
        Assert.Equal(StringExtractor.ExtractAll().Select(entry => entry.Text.Replace("\r", "")).Distinct(), inventory.Select(entry => entry.Text));
        Assert.All(inventory, entry => Assert.NotEmpty(entry.Uses));
        foreach (var domain in LocalizationOwnership.Modules.Select(module => module.Domain).Prepend(LocalizationOwnership.Core).Append(LocalizationOwnership.InvariantExport))
            output.WriteLine($"{domain}: {inventory.Count(entry => entry.Domains.Contains(domain))} keys; {inventory.Count(entry => entry.Domains.Contains(domain) && entry.Domains.Count > 1)} shared.");
        output.WriteLine($"Total unique keys: {inventory.Count}; shared ownership: {inventory.Count(entry => entry.Domains.Count > 1)}.");
        var name = Assert.Single(inventory, entry => entry.Text == "Name");
        Assert.True(name.RetainInCore);
        Assert.Contains("dausuva.automation.scheduling", name.Domains);
    }

    [Theory]
#if REPORTING_MODULE
    [InlineData("dausuva.reporting.inventory", "Export inventory")]
#endif
#if BROWSER_INVENTORY_MODULE
    [InlineData("dausuva.browser.inventory", "Browser profiles were not read; their state is unknown.")]
#endif
    [InlineData("dausuva.automation.scheduling", "Scheduled actions")]
    [InlineData("dausuva.student-work.backup", "{0} file(s) copied, {1} already there")]
    [InlineData("dausuva.appearance.inventory", "Screen saver")]
    [InlineData("dausuva.software.inventory", "Same version on all checked PCs")]
    [InlineData("dausuva.maintenance.inventory", "Unnamed update")]
    public void Every_module_has_a_reviewed_presentation_owner_even_when_source_is_in_Host(string domain, string key)
    {
        var entry = Assert.Single(Inventory.Value, entry => entry.Text == key);
        Assert.Contains(entry.Uses, use => use.Domain == domain && use.File.StartsWith("ClassroomHost/", StringComparison.Ordinal));
        Assert.Contains(LocalizationOwnership.Modules, module => module.Domain == domain);
    }

    [Fact]
    public void Core_transport_service_recovery_and_mixed_file_action_text_is_not_reassigned()
    {
        var inventory = Inventory.Value;
        Assert.All(inventory.SelectMany(entry => entry.Uses).Where(use => use.File.StartsWith("Shared/", StringComparison.Ordinal) || use.File.StartsWith("ClassroomClient/", StringComparison.Ordinal)),
            use => Assert.Equal(LocalizationOwnership.Core, use.Domain));
        foreach (var key in new[] { LocalizationOwnership.ScreenSaverTrustWarning, "Backup scan returned no pagination progress.", "No result was recorded.", "Uninstall {0} + {1} more" })
        {
            var entry = inventory.SingleOrDefault(entry => entry.Text == key);
            Assert.True(entry is not null && entry.RetainInCore, key);
        }
        Assert.True(Assert.Single(inventory, entry => entry.Text == "Screen saver").RetainInCore);
    }

    [Fact]
    public void Ownership_does_not_drop_existing_Lithuanian_translations_or_invent_new_entries()
    {
        var translated = PoFile.Parse(LanguageFiles.BuiltIn(template: false))
            .Where(entry => entry.Id.Length > 0).ToDictionary(entry => entry.Id, entry => entry.Text);
        // Texts owned only by a module that this build leaves out (ClassroomModules.props) are not in its table.
        var absent = ModuleLocalizationSources.Modules.Select(module => module.Domain).Where(domain => BuiltInModules.Registry.Find(domain) is null).ToHashSet();
        var left = Inventory.Value.Where(entry => entry.Domains.Count > 0 && entry.Domains.All(absent.Contains)).Select(entry => entry.Text).ToHashSet();
        var expected = Inventory.Value.Where(entry => !left.Contains(entry.Text)).ToArray();
        Assert.All(expected, entry => Assert.False(string.IsNullOrWhiteSpace(translated[entry.Text]), entry.Text));
        Assert.Equal(translated.Keys.Where(text => !left.Contains(text)).Order(), expected.Select(entry => entry.Text).Order());
    }
}
