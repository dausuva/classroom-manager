// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Reporting;

namespace ClassroomManager.Host;

/// <summary>Maps only inventory fields into the reporting module; transport and credentials stay in the Host.</summary>
public static class InventoryReportData
{
    public static InventorySnapshot Capture(JsonObject data) => new(
        new InventoryHardware(Text(data, "os"), Text(data, "osArchitecture"), Text(data, "manufacturer"), Text(data, "model"),
            Text(data, "cpu"), data["cores"]!.GetValue<int>(), data["logicalProcessors"]!.GetValue<int>(),
            data["totalMemoryMb"]!.GetValue<double>(),
            data["disks"]!.AsArray().Select(disk => new InventoryDisk(Text(disk!, "drive"),
                disk!["freeGb"]!.GetValue<double>(), disk["sizeGb"]!.GetValue<double>())).ToArray()),
        data["programs"]!.AsArray().Select(program => new InventoryProgram(
            Text(program!, "name"), Text(program!, "version"), Text(program!, "publisher"))).ToArray());

    static string Text(JsonNode node, string key) => node[key]!.GetValue<string>();
}
