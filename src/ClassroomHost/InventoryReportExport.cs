// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Reporting;

namespace ClassroomManager.Host;

/// <summary>Host-owned output adapter for a report and the folder the user approved.</summary>
public sealed class InventoryReportExport
{
    readonly object _gate = new();
    readonly InventoryReportSession _report;
    bool _exportRequested;

    public string HardwarePath { get; }
    public string SoftwarePath { get; }

    public InventoryReportExport(InventoryReportSession report, string approvedFolder, DateTime timestamp)
    {
        _report = report;
        var stamp = timestamp.ToString("yyyy-MM-dd_HHmm", System.Globalization.CultureInfo.InvariantCulture);
        HardwarePath = Path.Combine(approvedFolder, $"inventory-hardware-{stamp}.csv");
        SoftwarePath = Path.Combine(approvedFolder, $"inventory-software-{stamp}.csv");
    }

    public void Include(string targetId, InventorySnapshot snapshot)
    {
        lock (_gate)
        {
            _report.Include(targetId, snapshot);
            // Retry failed retains this adapter through OperationActivity; refresh the approved files too.
            if (_exportRequested) WriteCore();
        }
    }

    public InventoryCsvReport Write()
    {
        lock (_gate)
        {
            _exportRequested = true;
            return WriteCore();
        }
    }

    InventoryCsvReport WriteCore()
    {
        var csv = _report.Build();
        File.WriteAllLines(HardwarePath, csv.HardwareRows);
        File.WriteAllLines(SoftwarePath, csv.SoftwareRows);
        return csv;
    }
}
