// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ProgramInventoryComparisonTests
{
    [Fact]
    public void Build_reports_version_and_coverage_differences()
    {
        var first = new ClientInfo { Address = "10.0.0.1", Name = "PC-1" };
        var second = new ClientInfo { Address = "10.0.0.2", Name = "PC-2" };
        var third = new ClientInfo { Address = "10.0.0.3", Name = "PC-3" };
        var inventories = new Dictionary<ClientInfo, IReadOnlyList<ProgramEntry>>
        {
            [first] = [Program("Browser", "1.0", true), Program("Editor", "4.0", true)],
            [second] = [Program("browser", "2.0", false), Program("Editor", "4.0", true)],
            [third] = [Program("Editor", "4.0", true)],
        };

        var compared = ProgramInventoryComparison.Build([first, second, third], inventories);

        var browser = Assert.Single(compared, item => item.Name.Equals("Browser", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("1.0, 2.0", browser.Versions);
        Assert.Equal("2 / 3 checked", browser.Installed);
        Assert.Equal("1 / 2", browser.Silent);
        Assert.True(browser.HasVersionDifference);
        Assert.True(browser.IsMissingFromSome);
        Assert.Equal("PC-3", browser.MissingComputers);
        Assert.Contains("PC-1: 1.0", browser.Computers);
        Assert.Contains("PC-2: 2.0", browser.Computers);
        Assert.Contains("Versions differ", browser.Comparison);
        Assert.Contains("Missing on 1 checked PC", browser.Comparison);

        var editor = Assert.Single(compared, item => item.Name == "Editor");
        Assert.False(editor.HasDifference);
        Assert.Equal("Same version on all checked PCs", editor.Comparison);
    }

    [Fact]
    public void Build_marks_failed_inventory_as_unavailable_not_missing()
    {
        var online = new ClientInfo { Address = "10.0.0.1", Name = "Online" };
        var offline = new ClientInfo { Address = "10.0.0.2", Name = "Offline" };
        var inventories = new Dictionary<ClientInfo, IReadOnlyList<ProgramEntry>>
        {
            [online] = [Program("Browser", "1.0", true)],
        };

        var browser = Assert.Single(ProgramInventoryComparison.Build([online, offline], inventories));

        Assert.False(browser.IsMissingFromSome);
        Assert.Contains("1 unavailable", browser.Comparison);
        Assert.Equal("1 / 1 checked", browser.Installed);
    }

    [Fact]
    public void Uninstall_title_names_products_and_counts_the_rest()
    {
        Assert.Equal("Uninstall Browser", ProgramUninstallOutcome.Title(["Browser"]));
        Assert.Equal("Uninstall A, B, C", ProgramUninstallOutcome.Title(["A", "B", "C"]));
        Assert.Equal("Uninstall A, B, C + 2 more", ProgramUninstallOutcome.Title(["A", "B", "C", "D", "E"]));
    }

    [Fact]
    public void Uninstall_failure_message_is_empty_only_when_every_product_succeeded()
    {
        Assert.Equal("", ProgramUninstallOutcome.FailureMessage(2, []));
        Assert.Equal("1 of 3 uninstall(s) failed — Browser: exit code 1603",
            ProgramUninstallOutcome.FailureMessage(3, ["Browser: exit code 1603"]));
        Assert.Equal("2 of 2 uninstall(s) failed — Browser: timed out; Editor: access denied",
            ProgramUninstallOutcome.FailureMessage(2, ["Browser: timed out", "Editor: access denied"]));
    }

    static ProgramEntry Program(string name, string version, bool silent) => new()
    {
        Key = name,
        Name = name,
        Version = version,
        Publisher = "Publisher",
        Silent = silent,
    };
}
