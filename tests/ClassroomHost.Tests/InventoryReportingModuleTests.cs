// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Reporting;
using Xunit;

namespace ClassroomHost.Tests;

// T-222: first extracted feature module; typed selected snapshots and CSV output.
public sealed class InventoryReportingModuleTests
{
    static InventorySnapshot Snapshot(string program = "Editor") => new(
        new InventoryHardware("Windows 10", "64-bit", "Example", "Desktop", "CPU", 4, 8, 8192,
            [new InventoryDisk("C:", 20.5, 100)]),
        [new InventoryProgram(program, "1.0", "Publisher")]);

    [Fact]
    public void Normal_report_preserves_existing_headers_and_rows()
    {
        var report = InventoryReportingModule.CreateReport([new("one", "PC-1")]);
        report.Include("one", Snapshot());
        var result = report.Build();
        Assert.Equal("Computer,OS,Architecture,Manufacturer,Model,CPU,Cores,LogicalProcessors,TotalMemoryMb,Disks", result.HardwareRows[0]);
        Assert.Equal("PC-1,Windows 10,64-bit,Example,Desktop,CPU,4,8,8192,C:: 20.5/100 GB free", result.HardwareRows[1]);
        Assert.Equal(["Computer,Program,Version,Publisher", "PC-1,Editor,1.0,Publisher"], result.SoftwareRows);
        Assert.Equal((1, 1), (result.Computers, result.Programs));
    }

    [Fact]
    public void Scope_comes_only_from_selected_targets_and_order_does_not_depend_on_completion_order()
    {
        var report = InventoryReportingModule.CreateReport([new("one", "Same label"), new("two", "Same label"), new("offline", "Offline")]);
        report.Include("two", Snapshot("Second"));
        report.Include("one", Snapshot("First"));
        Assert.Throws<ArgumentOutOfRangeException>(() => report.Include("not-selected", Snapshot()));
        var result = report.Build();
        Assert.Equal(2, result.Computers);
        Assert.Contains("First", result.SoftwareRows[1]);
        Assert.Contains("Second", result.SoftwareRows[2]);
        Assert.DoesNotContain(result.HardwareRows, row => row.Contains("Offline"));
    }

    [Fact]
    public void Duplicate_or_empty_target_ids_are_refused()
    {
        Assert.Throws<ArgumentException>(() => InventoryReportingModule.CreateReport([new("one", "First"), new("one", "Second")]));
        Assert.Throws<ArgumentException>(() => InventoryReportingModule.CreateReport([new("", "First")]));
    }

    [Fact]
    public void A_retry_replaces_rows_instead_of_counting_the_computer_twice()
    {
        var report = InventoryReportingModule.CreateReport([new("one", "PC-1")]);
        report.Include("one", Snapshot("Old"));
        report.Include("one", Snapshot("New"));
        var result = report.Build();
        Assert.Equal((1, 1), (result.Computers, result.Programs));
        Assert.Contains("New", result.SoftwareRows[1]);
        Assert.DoesNotContain(result.SoftwareRows, row => row.Contains("Old"));
    }

    [Fact]
    public void Input_collections_and_prior_results_are_detached()
    {
        InventoryReportTarget[] targets = [new("one", "PC-1")];
        var report = InventoryReportingModule.CreateReport(targets);
        targets[0] = new("foreign", "Other");
        var programs = new List<InventoryProgram> { new("Editor", "1", "Publisher") };
        var disks = new List<InventoryDisk> { new("C:", 10, 20) };
        report.Include("one", Snapshot() with { Programs = programs, Hardware = Snapshot().Hardware with { Disks = disks } });
        programs.Clear();
        disks.Clear();
        var before = report.Build();
        report.Include("one", Snapshot("Changed"));
        Assert.Contains("Editor", before.SoftwareRows[1]);
        Assert.Contains("C:: 10/20", before.HardwareRows[1]);
        Assert.Contains("PC-1", before.HardwareRows[1]);
    }

    [Fact]
    public async Task Parallel_results_are_aggregated_once_per_selected_computer()
    {
        var targets = Enumerable.Range(0, 100).Select(i => new InventoryReportTarget(i.ToString(), "PC-" + i)).ToArray();
        var report = InventoryReportingModule.CreateReport(targets);
        await Task.WhenAll(targets.Select(target => Task.Run(() => report.Include(target.Id, Snapshot()))));
        Assert.Equal((100, 100), (report.Build().Computers, report.Build().Programs));
    }

    [Fact]
    public void Csv_numbers_are_identical_in_Lithuanian_and_English_cultures()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            var report = InventoryReportingModule.CreateReport([new("one", "PC-1")]);
            report.Include("one", Snapshot());
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("lt-LT");
            var lithuanian = report.Build().HardwareRows;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal(lithuanian, report.Build().HardwareRows);
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("a\rb", "\"a\rb\"")]
    [InlineData("a\nb", "\"a\nb\"")]
    [InlineData("=1+1", "\"\t=1+1\"")]
    [InlineData(" +SUM(A1)", "\"\t +SUM(A1)\"")]
    [InlineData("@SUM(A1)", "\"\t@SUM(A1)\"")]
    [InlineData("-1+1", "\"\t-1+1\"")]
    [InlineData("＝1+1", "\"\t＝1+1\"")]
    public void Untrusted_cell_text_is_quoted_and_formula_prefixes_are_treated_as_text(string text, string expected)
    {
        var report = InventoryReportingModule.CreateReport([new("one", "PC-1")]);
        report.Include("one", Snapshot(text));
        Assert.Equal($"PC-1,{expected},1.0,Publisher", report.Build().SoftwareRows[1]);
    }

#if REPORTING_MODULE
    [Fact]
    public void Adapter_allows_only_inventory_fields()
    {
        var data = JsonNode.Parse("""{"os":"Windows","osArchitecture":"x86","manufacturer":"Example","model":"PC","cpu":"CPU","cores":2,"logicalProcessors":4,"totalMemoryMb":4096,"disks":[],"programs":[],"psk":"fixture-never-export","unrelated":"fixture-private"}""")!.AsObject();
        var report = InventoryReportingModule.CreateReport([new("one", "PC-1")]);
        report.Include("one", InventoryReportData.Capture(data));
        var csv = report.Build();
        Assert.Equal((1, 0), (csv.Computers, csv.Programs));
        Assert.DoesNotContain("fixture-never-export", string.Join("\n", csv.HardwareRows.Concat(csv.SoftwareRows)));
        Assert.DoesNotContain("fixture-private", string.Join("\n", csv.HardwareRows.Concat(csv.SoftwareRows)));
    }

    [Fact]
    public void A_malformed_inventory_does_not_add_partial_rows()
    {
        var data = JsonNode.Parse("""{"os":"Windows","osArchitecture":"x86","manufacturer":"Example","model":"PC","cpu":"CPU","cores":2,"logicalProcessors":4,"totalMemoryMb":4096,"disks":[],"programs":{}}""")!.AsObject();
        var report = InventoryReportingModule.CreateReport([new("one", "PC-1")]);
        Assert.Throws<InvalidOperationException>(() => report.Include("one", InventoryReportData.Capture(data)));
        Assert.Equal((0, 0), (report.Build().Computers, report.Build().Programs));
    }
#endif

    [Fact]
    public void Module_assembly_references_only_framework_assemblies_and_the_module_contract()
    {
        var references = typeof(InventoryReportingModule).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name!.StartsWith("Classroom", StringComparison.Ordinal) && reference.Name != "ClassroomManager.Modules.Contract");
        Assert.DoesNotContain(references, reference => reference.Name!.StartsWith("Presentation", StringComparison.Ordinal));
    }
}
