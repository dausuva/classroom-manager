// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Maintenance;

public sealed record UpdateMetadata(string Title, IReadOnlyList<string> Kb, long Size, bool Feature, bool Optional, bool RequiresRestart);
public sealed record UpdateProjection(string Title, IReadOnlyList<string> Kb, double? SizeMb, bool Feature, bool Optional, bool RequiresRestart);
public sealed record UpdateScanReport(IReadOnlyList<UpdateProjection> Updates, int Features, int Optional, int RequiringRestart);
public sealed record UpdatePauseSnapshot(bool? Paused, DateTimeOffset? Until);
public sealed record UpdatePauseProjection(bool Paused, DateTimeOffset? Until);

/// <summary>Pure inventory projection; no scan execution, update identities or machine changes.</summary>
public static class MaintenanceInventoryModule
{
    public const string Id = "dausuva.maintenance.inventory";
    public const int ContractVersion = 1;

    public static UpdateScanReport ProjectScan(IReadOnlyList<UpdateMetadata> updates)
    {
        var projected = updates.Select(update => new UpdateProjection(update.Title,
            Array.AsReadOnly(update.Kb.Where(value => value.Length > 0).Select(value => "KB" + value.TrimStart('K', 'B')).ToArray()),
            update.Size > 0 ? update.Size / 1048576d : null,
            update.Feature, !update.Feature && update.Optional, update.RequiresRestart)).ToArray();
        return new(Array.AsReadOnly(projected), projected.Count(update => update.Feature),
            projected.Count(update => update.Optional), projected.Count(update => update.RequiresRestart));
    }

    public static UpdatePauseProjection ProjectPause(UpdatePauseSnapshot snapshot, bool pauseWasRequested, DateTimeOffset now)
    {
        var paused = snapshot.Paused ?? (pauseWasRequested && snapshot.Until is { } until && until > now);
        return new(paused, paused ? snapshot.Until : null);
    }
}
