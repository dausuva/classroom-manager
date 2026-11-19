// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Maintenance;

namespace ClassroomManager.Host;

/// <summary>Core-owned wire allowlist and display adapter for the built-in maintenance projection.</summary>
public static class WindowsMaintenanceInventory
{
    public static UpdateScanReport ProjectScan(JsonObject data) => MaintenanceOperations.ReadScan(data.ToJsonString(), MaintenancePresentation.Unnamed);

    public static string Details(UpdateProjection update) => MaintenancePresentation.Details(update);

    public static UpdatePauseProjection ProjectPause(JsonObject data, bool pauseWasRequested, DateTimeOffset now) =>
        MaintenanceOperations.ReadPause(data.ToJsonString(), pauseWasRequested, now);
}
