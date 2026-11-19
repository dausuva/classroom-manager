// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using ClassroomManager.Host;
using ClassroomManager.SoftwareInventory;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class SoftwareInventoryModuleTests
{
    [Fact]
    public void Duplicate_registry_entries_count_one_computer_and_keep_tokens()
    {
        var report = Assert.Single(SoftwareInventoryModule.Compare([new(0, "PC"), new(1, "Missing"), new(2, "Unavailable")],
            [new(0, [new(0, " Browser ", " 1 ", " A ", true), new(1, "browser", "2", "a", false)]), new(1, [])]));
        Assert.Equal("Browser", report.Name);
        Assert.Equal(["1", "2"], report.Versions);
        Assert.Equal(["A"], report.Publishers);
        Assert.Equal(1, report.Installed);
        Assert.Equal(2, report.Checked);
        Assert.Equal(1, report.Unavailable);
        Assert.Equal(1, report.Silent);
        Assert.Equal([0, 1], report.EntryTokens);
        Assert.Equal(["Missing"], report.MissingComputers);
    }

    [Fact]
    public void Empty_reads_and_unknown_versions_are_not_fabricated()
    {
        Assert.Empty(SoftwareInventoryModule.Compare([new(0, "PC")], [new(0, [])]));
        Assert.Empty(SoftwareInventoryModule.Compare([new(0, "PC")], []));
        var report = Assert.Single(SoftwareInventoryModule.Compare([new(0, "PC")], [new(0, [new(0, "App", " ", "", false)])]));
        Assert.Empty(report.Versions);
        Assert.Empty(Assert.Single(report.Computers).Versions);
    }

    [Fact]
    public void Off_scope_duplicate_targets_and_duplicate_tokens_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => SoftwareInventoryModule.Compare([new(0, "PC")], [new(1, [])]));
        Assert.Throws<ArgumentException>(() => SoftwareInventoryModule.Compare([new(0, "PC"), new(0, "Other")], []));
        Assert.Throws<ArgumentException>(() => SoftwareInventoryModule.Compare([new(0, "PC")], [new(0, []), new(0, [])]));
        Assert.Throws<ArgumentException>(() => SoftwareInventoryModule.Compare([new(0, "PC")],
            [new(0, [new(0, "A", "", "", false), new(0, "B", "", "", false)])]));
    }

    [Fact]
    public void Result_collections_are_detached_and_read_only()
    {
        var programs = new List<SoftwareProgram> { new(0, "App", "1", "P", false) };
        var result = SoftwareInventoryModule.Compare([new(0, "PC")], [new(0, programs)]);
        programs.Clear();
        var report = Assert.Single(result);
        Assert.Equal(["1"], report.Versions);
        Assert.Throws<NotSupportedException>(() => ((IList<SoftwareComparison>)result).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<int>)report.EntryTokens).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)Assert.Single(report.Computers).Versions).Clear());
    }

    [Fact]
    public void Core_retains_action_entries_but_module_contract_excludes_action_fields()
    {
        var client = new ClientInfo { Name = "PC" };
        var program = new ProgramEntry { Name = "App", Key = "private-key", UninstallString = "private-command", InstallLocation = "private-path" };
        var report = Assert.Single(ProgramInventoryComparison.Build([client], new Dictionary<ClientInfo, IReadOnlyList<ProgramEntry>> { [client] = [program] }));
        Assert.Same(program, Assert.Single(report.Entries).Program);
        Assert.Equal("PC: unknown", report.Computers);
        Assert.Equal("(unknown)", report.Versions);
        var dto = new SoftwareProgram(0, program.Name, program.Version, program.Publisher, program.Silent);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(dto));
        Assert.Equal(["Name", "Publisher", "Silent", "Token", "Version"], typeof(SoftwareProgram).GetProperties().Select(property => property.Name).Order());
    }

    [Fact]
    public void Module_references_only_framework_assemblies() =>
        FrameworkReferenceAssertions.AssertOnly(typeof(SoftwareInventoryModule).Assembly);
}
