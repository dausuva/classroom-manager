// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using ClassroomManager.Modules;

namespace ClassroomManager.Reporting;

public sealed record InventoryReportTarget(string Id, string Computer);
public sealed record InventoryDisk(string Drive, double FreeGb, double SizeGb);
public sealed record InventoryProgram(string Name, string Version, string Publisher);
public sealed record InventoryHardware(string OS, string Architecture, string Manufacturer, string Model, string CPU,
    int Cores, int LogicalProcessors, double TotalMemoryMb, IReadOnlyList<InventoryDisk> Disks);
public sealed record InventorySnapshot(InventoryHardware Hardware, IReadOnlyList<InventoryProgram> Programs);
public sealed record InventoryCsvReport(IReadOnlyList<string> HardwareRows, IReadOnlyList<string> SoftwareRows, int Computers, int Programs);

/// <summary>Built-in reporting module. Receives selected, authorized data; has no connection or file access.</summary>
public static class InventoryReportingModule
{
    public const string Id = "dausuva.reporting.inventory";
    public const int ContractVersion = 1;

    public static InventoryReportSession CreateReport(IEnumerable<InventoryReportTarget> selectedTargets) => new(selectedTargets);
}

/// <summary>The module's entry for the Host: says who the module is. The report itself needs nothing from a Client.</summary>
public sealed class InventoryReportingModuleEntry : IClassroomModule
{
    public ModuleDescriptor Describe() =>
        new(InventoryReportingModule.Id, "Inventory CSV export", ModuleContract.CurrentVersion, ReportingOperations.Commands)
        {
            Ports = [new(ModulePorts.ClientCall, 1)],
            SystemCommands = ReportingOperations.Commands,
            Icon =
            [
                @"+------+------+",
                @"| name | cpu  |",
                @"+------+------+",
                @"| pc01 | i5   |",
                @"+------+------+",
            ],
        };
}

/// <summary>One report invocation. Owns detached snapshots; successful retries replace the same target's prior result.</summary>
public sealed class InventoryReportSession
{
    readonly object _gate = new();
    readonly InventoryReportTarget[] _targets;
    readonly HashSet<string> _scope;
    readonly Dictionary<string, InventorySnapshot> _snapshots = new(StringComparer.Ordinal);

    internal InventoryReportSession(IEnumerable<InventoryReportTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _targets = targets.ToArray();
        if (_targets.Any(target => target is null || string.IsNullOrWhiteSpace(target.Id))) throw new ArgumentException(nameof(targets));
        _scope = _targets.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
        if (_scope.Count != _targets.Length) throw new ArgumentException(nameof(targets));
    }

    public void Include(string targetId, InventorySnapshot snapshot)
    {
        if (!_scope.Contains(targetId)) throw new ArgumentOutOfRangeException(nameof(targetId));
        ArgumentNullException.ThrowIfNull(snapshot);
        // Complete validation/copy before publishing; malformed data cannot replace a previous good result.
        var hardware = snapshot.Hardware;
        var detached = snapshot with
        {
            Hardware = hardware with { Disks = Array.AsReadOnly(hardware.Disks.ToArray()) },
            Programs = Array.AsReadOnly(snapshot.Programs.ToArray()),
        };
        if (detached.Hardware.Disks.Any(disk => disk is null) || detached.Programs.Any(program => program is null))
            throw new ArgumentException(nameof(snapshot));
        lock (_gate) _snapshots[targetId] = detached;
    }

    public InventoryCsvReport Build()
    {
        KeyValuePair<InventoryReportTarget, InventorySnapshot>[] snapshots;
        lock (_gate)
            snapshots = _targets.Where(target => _snapshots.ContainsKey(target.Id))
                .Select(target => new KeyValuePair<InventoryReportTarget, InventorySnapshot>(target, _snapshots[target.Id])).ToArray();

        var hardware = new List<string> { "Computer,OS,Architecture,Manufacturer,Model,CPU,Cores,LogicalProcessors,TotalMemoryMb,Disks" };
        var software = new List<string> { "Computer,Program,Version,Publisher" };
        foreach (var (target, data) in snapshots)
        {
            var h = data.Hardware;
            var disks = string.Join(" | ", h.Disks.Select(disk =>
                $"{disk.Drive}: {disk.FreeGb.ToString(CultureInfo.InvariantCulture)}/{disk.SizeGb.ToString(CultureInfo.InvariantCulture)} GB free"));
            hardware.Add(Csv(target.Computer, h.OS, h.Architecture, h.Manufacturer, h.Model, h.CPU,
                h.Cores.ToString(CultureInfo.InvariantCulture), h.LogicalProcessors.ToString(CultureInfo.InvariantCulture),
                h.TotalMemoryMb.ToString("0", CultureInfo.InvariantCulture), disks));
            foreach (var program in data.Programs)
                software.Add(Csv(target.Computer, program.Name, program.Version, program.Publisher));
        }
        return new(Array.AsReadOnly(hardware.ToArray()), Array.AsReadOnly(software.ToArray()), snapshots.Length, software.Count - 1);
    }

    static string Csv(params string[] fields) => string.Join(",", fields.Select(Field));

    static string Field(string value)
    {
        value ??= "";
        var trimmed = value.AsSpan().TrimStart();
        // These exports are intended for spreadsheet viewing. Keep untrusted text out of formula cells.
        var protect = trimmed.Length > 0 && "=+-@＝＋－＠".Contains(trimmed[0]) ||
                      value.Length > 0 && value[0] is '\t' or '\r' or '\n';
        if (protect) value = "\t" + value;
        return protect || value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
