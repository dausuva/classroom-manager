// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Reporting;
using Xunit;

namespace ClassroomHost.Tests;

// T-222: Host-owned output and retry integration for the reporting module.
public sealed class InventoryReportExportTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("cm-reporting-").FullName;
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    static InventorySnapshot Snapshot(string program) => new(
        new InventoryHardware("Windows", "x86", "Example", "PC", "CPU", 2, 4, 4096, []),
        [new InventoryProgram(program, "1", "Publisher")]);

    InventoryReportExport Export() => new(InventoryReportingModule.CreateReport([new("one", "PC-1"), new("two", "PC-2")]),
        _folder, new DateTime(2026, 10, 26, 12, 30, 0));

    [Fact]
    public void Writes_both_existing_file_formats_and_retry_refreshes_the_same_approved_files()
    {
        var export = Export();
        export.Include("one", Snapshot("Editor"));
        Assert.Empty(Directory.GetFiles(_folder));
        Assert.Equal((1, 1), (export.Write().Computers, export.Write().Programs));
        Assert.EndsWith("inventory-hardware-2026-10-26_1230.csv", export.HardwarePath);
        Assert.EndsWith("inventory-software-2026-10-26_1230.csv", export.SoftwarePath);
        export.Include("two", Snapshot("Retry result"));
        export.Include("one", Snapshot("Updated"));
        Assert.Equal(3, File.ReadAllLines(export.HardwarePath).Length);
        var rows = File.ReadAllLines(export.SoftwarePath);
        Assert.Equal(3, rows.Length);
        Assert.Contains("PC-2,Retry result,1,Publisher", rows);
        Assert.Contains("PC-1,Updated,1,Publisher", rows);
        Assert.DoesNotContain(rows, row => row.Contains("Editor"));
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void Output_failure_is_reported_and_a_later_retry_can_finish_the_exports()
    {
        var export = Export();
        Directory.CreateDirectory(export.SoftwarePath);
        export.Include("one", Snapshot("Editor"));
        Assert.Throws<UnauthorizedAccessException>(() => export.Write());
        Directory.Delete(export.SoftwarePath);
        export.Include("two", Snapshot("Retry result"));
        Assert.Equal(3, File.ReadAllLines(export.HardwarePath).Length);
        Assert.Equal(3, File.ReadAllLines(export.SoftwarePath).Length);
    }
}
