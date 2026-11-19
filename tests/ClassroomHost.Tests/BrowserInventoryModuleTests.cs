// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.BrowserInventory;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class BrowserInventoryModuleTests
{
    static readonly BrowserIdentity[] Main = [new("chrome", "Chrome"), new("edge", "Edge")];
    static BrowserInventoryResult Answer(int id, InventoryReadStatus status = InventoryReadStatus.Success,
        BrowserSnapshot? browsers = null, IReadOnlyList<BrowserProfile>? profiles = null) =>
        new(new(id, $"PC-{id}", true, true, true),
            new(status, status == InventoryReadStatus.Success ? [new("profiles", "on"), new("exit", "partly")] : null),
            new(status, status == InventoryReadStatus.Success ? browsers ?? new([Main[0]], "chrome", "edge") : null),
            new(status, status == InventoryReadStatus.Success ? profiles ?? [] : null));
    static BrowserInventoryReport Project(params BrowserInventoryResult[] answers) =>
        BrowserInventoryModule.Project(answers, Main, Main, ["profiles", "exit"]);

    [Fact]
    public void Pure_report_counts_only_successful_reads_and_preserves_selection_order()
    {
        var report = Project(Answer(2), Answer(1, InventoryReadStatus.Unavailable),
            Answer(0, browsers: new([Main[1], new("other", "Other")], "other", "")));
        Assert.Equal(3, report.Total);
        Assert.Equal(2, report.BrowsersRead);
        Assert.Equal(2, report.PoliciesRead);
        Assert.Equal(2, report.ProfilesRead);
        Assert.Equal(["PC-0"], report.MainBrowsers[0].Missing);
        Assert.Equal(["PC-2"], report.MainBrowsers[1].Missing);
        Assert.All(report.MainBrowsers, browser => Assert.Equal(1, browser.Present));
        Assert.Equal(new BrowserCount("Other", 1), Assert.Single(report.Others));
        Assert.Equal([new BrowserCount("Chrome", 1), new BrowserCount("other", 1)], report.Defaults);
        Assert.Equal(new BrowserCount("Edge", 1), Assert.Single(report.Managed));
        Assert.Equal([new BrowserPolicyCount("profiles", 2), new BrowserPolicyCount("exit", 0)], report.Policies);
        Assert.Equal(1, Assert.Single(report.Incomplete).Target.Id);
        Assert.Equal(["chrome", "edge", "other"], report.DefaultChoices.Select(browser => browser.Id));
    }

    [Theory]
    [InlineData(InventoryReadStatus.Failed)]
    [InlineData(InventoryReadStatus.Unavailable)]
    [InlineData(InventoryReadStatus.Unsupported)]
    [InlineData(InventoryReadStatus.NoUser)]
    public void Unknown_is_not_successful_empty_inventory(InventoryReadStatus status)
    {
        var report = Project(Answer(0, status));
        Assert.Equal(0, report.BrowsersRead);
        Assert.Equal(0, report.PoliciesRead);
        Assert.Equal(0, report.ProfilesRead);
        Assert.Empty(report.ExtraProfiles);
        Assert.Empty(report.Defaults);
        Assert.Single(report.Incomplete);
    }

    [Fact]
    public void Extra_profiles_exclude_each_browsers_first_and_keep_existing_display_metadata()
    {
        var extra = new BrowserProfile("Chrome", "School", "student@example.test", false);
        var report = Project(Answer(0, profiles: [new("Chrome", "Default", "", true), extra]));
        Assert.Equal(extra, Assert.Single(Assert.Single(report.ExtraProfiles).Profiles));
        Assert.Equal("Chrome \"School\" (student@example.test)", extra.ToString());
        Assert.Equal(1, report.ProfilesRead);
    }

    [Fact]
    public void Default_choices_deduplicate_ids_case_insensitively_and_keep_main_choices_without_results()
    {
        Assert.Equal(Main, Project().DefaultChoices);
        var report = Project(Answer(0, browsers: new([new("CHROME", "Duplicate"), new("other", "Other")], "", "")),
            Answer(1, browsers: new([new("OTHER", "Duplicate other")], "", "")));
        Assert.Equal(["chrome", "edge", "other"], report.DefaultChoices.Select(browser => browser.Id));
        Assert.DoesNotContain(report.Others, browser => browser.Name == "Duplicate");
    }

    sealed class Reader : IBrowserInventoryReader
    {
        public List<BrowserIdentity> Browsers { get; } = [Main[0]];
        public List<BrowserPolicy> Policies { get; } = [new("profiles", "on")];
        public List<BrowserProfile> Profiles { get; } = [new("Chrome", "Default", "", true)];
        public bool Disposed { get; private set; }
        public Task<IReadOnlyList<BrowserPolicy>> ReadPoliciesAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<BrowserPolicy>>(Policies);
        public Task<BrowserSnapshot> ReadBrowsersAsync(bool hasUser, CancellationToken cancellation) => Task.FromResult(new BrowserSnapshot(Browsers, "", ""));
        public Task<IReadOnlyList<BrowserProfile>> ReadProfilesAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<BrowserProfile>>(Profiles);
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Workflow_copies_provider_collections_before_releasing_reader()
    {
        var reader = new Reader();
        var workflow = new BrowserInventoryWorkflow((_, _) => Task.FromResult<IBrowserInventoryReader>(reader));
        var answers = await workflow.ReadAsync([new(0, "PC", true, true, true)]);
        reader.Browsers.Clear();
        reader.Policies.Clear();
        reader.Profiles.Clear();
        Assert.Single(answers[0].Browsers.Value!.Installed);
        Assert.Single(answers[0].Policies.Value!);
        Assert.Single(answers[0].Profiles.Value!);
        Assert.True(reader.Disposed);
        Assert.Throws<NotSupportedException>(() => ((IList<BrowserIdentity>)answers[0].Browsers.Value!.Installed).Clear());
    }

    [Fact]
    public async Task Duplicate_selected_ids_are_rejected_before_opening_any_reader()
    {
        var opened = 0;
        var workflow = new BrowserInventoryWorkflow((_, _) => { opened++; return Task.FromResult<IBrowserInventoryReader>(new Reader()); });
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.ReadAsync([new(0, "A", true, true, true), new(0, "B", true, true, true)]));
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task Selected_scope_is_copied_before_asynchronous_connections_complete()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = new List<BrowserInventoryTarget> { new(0, "Chosen", true, true, true) };
        var workflow = new BrowserInventoryWorkflow(async (_, cancellation) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellation);
            return new Reader();
        });
        var pending = workflow.ReadAsync(selected);
        await entered.Task;
        selected[0] = new(1, "Not chosen", true, true, true);
        release.SetResult();
        Assert.Equal("Chosen", Assert.Single(await pending).Target.Computer);
    }

    [Fact]
    public void Module_has_only_framework_assembly_references_and_no_generic_command_hook()
    {
        var assembly = typeof(BrowserInventoryModule).Assembly;
        FrameworkReferenceAssertions.AssertOnly(assembly);
        Assert.Equal(["ReadBrowsersAsync", "ReadPoliciesAsync", "ReadProfilesAsync"],
            typeof(IBrowserInventoryReader).GetMethods().Select(method => method.Name).Order());
        Assert.DoesNotContain(assembly.GetExportedTypes(), type => type.Namespace?.StartsWith("System.Text.Json", StringComparison.Ordinal) == true);
    }
}
