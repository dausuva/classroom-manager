// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Management;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// A hardware + installed-software snapshot of this computer, for exporting an inventory across
/// the lab from the host. System-level only. Each WMI query is wrapped separately so one failing
/// class (locked down, missing provider, whatever) doesn't blank out the rest of the snapshot.
/// </summary>
sealed class InventoryHandler
{
    public Response Get()
    {
        var data = new JsonObject
        {
            ["machineName"] = Environment.MachineName,
            ["os"] = "",
            ["osArchitecture"] = "",
            ["manufacturer"] = "",
            ["model"] = "",
            ["cpu"] = "",
            ["cores"] = 0,
            ["logicalProcessors"] = Environment.ProcessorCount,
            ["totalMemoryMb"] = 0,
            ["disks"] = new JsonArray(),
        };

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption, Version, OSArchitecture FROM Win32_OperatingSystem");
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                data["os"] = $"{mo["Caption"]} ({mo["Version"]})";
                data["osArchitecture"] = mo["OSArchitecture"]?.ToString() ?? "";
                break;
            }
        }
        catch (Exception ex) { Log.Write($"inventory.get: OS query failed: {ex.Message}"); }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem");
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                data["manufacturer"] = mo["Manufacturer"]?.ToString() ?? "";
                data["model"] = mo["Model"]?.ToString() ?? "";
                if (ulong.TryParse(mo["TotalPhysicalMemory"]?.ToString(), out var bytes))
                    data["totalMemoryMb"] = Math.Round(bytes / 1024.0 / 1024.0, 0);
                break;
            }
        }
        catch (Exception ex) { Log.Write($"inventory.get: computer system query failed: {ex.Message}"); }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores FROM Win32_Processor");
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                data["cpu"] = mo["Name"]?.ToString()?.Trim() ?? "";
                if (int.TryParse(mo["NumberOfCores"]?.ToString(), out var cores))
                    data["cores"] = cores;
                break;
            }
        }
        catch (Exception ex) { Log.Write($"inventory.get: CPU query failed: {ex.Message}"); }

        try
        {
            var disks = new JsonArray();
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3");
            using var results = searcher.Get();
            foreach (ManagementBaseObject mo in results)
            {
                ulong.TryParse(mo["Size"]?.ToString(), out var size);
                ulong.TryParse(mo["FreeSpace"]?.ToString(), out var free);
                disks.Add(new JsonObject
                {
                    ["drive"] = mo["DeviceID"]?.ToString() ?? "",
                    ["sizeGb"] = Math.Round(size / 1024.0 / 1024.0 / 1024.0, 1),
                    ["freeGb"] = Math.Round(free / 1024.0 / 1024.0 / 1024.0, 1),
                });
            }
            data["disks"] = disks;
        }
        catch (Exception ex) { Log.Write($"inventory.get: disk query failed: {ex.Message}"); }

        try
        {
            var programs = new ProgramsHandler().List();
            data["programs"] = programs.Data["programs"]?.DeepClone() ?? new JsonArray();
        }
        catch (Exception ex) { Log.Write($"inventory.get: installed-programs query failed: {ex.Message}"); }

        return Response.Success(data);
    }
}
