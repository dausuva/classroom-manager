// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Modules;

namespace ClassroomManager.Maintenance;

/// <summary>
/// What the module asks of a computer about Windows Update, and how it reads the answers. It works only through the run's ports: every
/// command here is declared by the module and sent with administrator rights, and it never sees a connection, an address or a key.
/// The answers come back as the Client's JSON text so the Host can show every field it already shows.
/// </summary>
public sealed class MaintenanceOperations(IModuleClient client)
{
    public const string Scan = "windows.update.scan";
    public const string Status = "windows.update.status";
    public const string Pause = "windows.update.pause";
    public const string Resume = "windows.update.resume";
    public const string Install = "windows.update.install";

    /// <summary>The commands the module sends, all with administrator rights.</summary>
    public static IReadOnlyList<string> Commands { get; } = [Scan, Status, Pause, Resume, Install];

    // Windows Update works for minutes; the limits are the ones a person already waits for, and the broker caps them in any case.
    static readonly TimeSpan ScanTime = TimeSpan.FromMinutes(15);
    static readonly TimeSpan InstallTime = TimeSpan.FromMinutes(60);
    static readonly TimeSpan ShortTime = TimeSpan.FromMinutes(2);

    /// <summary>Looks for updates. <paramref name="includeOptional"/> null sends no choice and leaves it to the Client.</summary>
    public Task<string> ScanAsync(ModuleComputer computer, bool? includeOptional, CancellationToken cancellation) =>
        client.CallAsSystemAsync(computer, Scan, includeOptional is { } optional ? new JsonObject { ["includeOptional"] = optional }.ToJsonString() : "{}", cancellation, ScanTime);

    /// <summary>Downloads and installs everything the last scan found.</summary>
    public Task<string> InstallAllAsync(ModuleComputer computer, CancellationToken cancellation) =>
        client.CallAsSystemAsync(computer, Install, new JsonObject { ["all"] = true }.ToJsonString(), cancellation, InstallTime);

    public Task<string> StatusAsync(ModuleComputer computer, CancellationToken cancellation) =>
        client.CallAsSystemAsync(computer, Status, "{}", cancellation, ShortTime);

    public Task<string> PauseAsync(ModuleComputer computer, int days, CancellationToken cancellation) =>
        client.CallAsSystemAsync(computer, Pause, new JsonObject { ["days"] = days }.ToJsonString(), cancellation, ShortTime);

    public Task<string> ResumeAsync(ModuleComputer computer, CancellationToken cancellation) =>
        client.CallAsSystemAsync(computer, Resume, "{}", cancellation, ShortTime);

    /// <summary>Reads a scan answer into the module's projection. A missing title becomes <paramref name="unnamedTitle"/>.</summary>
    /// <exception cref="JsonException">The answer is not a list of updates.</exception>
    public static UpdateScanReport ReadScan(string answer, string unnamedTitle)
    {
        if (JsonNode.Parse(answer) is not JsonObject data || data["updates"] is not JsonArray updates) throw new JsonException("data");
        var metadata = updates.Select(node =>
        {
            if (node is not JsonObject update) throw new JsonException("data");
            var kb = update["kb"]?.AsArray().Select(value => value?.GetValue<string>() ?? "").ToArray() ?? [];
            return new UpdateMetadata(update["title"]?.GetValue<string>() ?? unnamedTitle, kb,
                update["size"]?.GetValue<long>() ?? 0, update["feature"]?.GetValue<bool>() == true,
                update["optional"]?.GetValue<bool>() == true, update["requiresRestart"]?.GetValue<bool>() == true);
        }).ToArray();
        return MaintenanceInventoryModule.ProjectScan(metadata);
    }

    /// <summary>The titles of the updates in a scan answer, without the unnamed ones; for a one-line summary.</summary>
    public static IReadOnlyList<string> ReadTitles(string answer) =>
        ((JsonNode.Parse(answer) as JsonObject)?["updates"] as JsonArray ?? [])
        .Select(update => update?["title"]?.GetValue<string>() ?? "").Where(title => title.Length > 0).ToList();

    /// <summary>Reads a status or pause answer. <paramref name="pauseWasRequested"/> is true right after a pause was asked for.</summary>
    public static UpdatePauseProjection ReadPause(string answer, bool pauseWasRequested, DateTimeOffset now)
    {
        var data = JsonNode.Parse(answer) as JsonObject ?? [];
        var until = data["untilUtc"]?.GetValue<string>() ?? "";
        return MaintenanceInventoryModule.ProjectPause(new(data["paused"]?.GetValue<bool>(),
            DateTimeOffset.TryParse(until, out var parsed) ? parsed : null), pauseWasRequested, now);
    }
}
