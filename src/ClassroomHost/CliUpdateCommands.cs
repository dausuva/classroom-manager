// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Maintenance;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>What ClassroomCli's <c>update</c> and <c>updateinstall</c> do on one computer (kept out of HostCli.cs, which may only shrink).</summary>
public static class CliUpdateCommands
{
    /// <summary>
    /// Downloads and installs every waiting Windows update through the Maintenance module. It never restarts the computer: when Windows asks for a
    /// restart the answer says so and the restart stays for the teacher (or a schedule of its own).
    /// </summary>
    public static async Task<string> InstallWindowsUpdatesAsync(ClientSession session, ClientInfo client, CancellationToken ct)
    {
        var (maintenance, machine) = ModuleRuns.OpenMaintenance(session, client);
        // The Client answers an install with an error when nothing is waiting, which a schedule would log as a failure on every computer that is up to date.
        if (MaintenanceOperations.ReadTitles(await maintenance.ScanAsync(machine, includeOptional: null, ct)).Count == 0) return "no updates to install";
        var answer = JsonNode.Parse(await maintenance.InstallAllAsync(machine, ct)) as JsonObject;
        var installed = answer?["selected"]?.GetValue<int>() ?? 0;
        var restartNeeded = answer?["rebootRequired"]?.GetValue<bool>() == true;
        return installed == 0 ? "no updates to install"
            : restartNeeded ? $"{installed} update(s) installed; restart needed (not done)"
            : $"{installed} update(s) installed";
    }

    /// <summary>Uploads a signed Client build and has the computer replace its own client with it.</summary>
    public static async Task<string> SendClientAsync(ClientSession session, HostSettings settings, CliOptions options)
    {
        // Asked first: a build for the other processor would not start (or would see the wrong registry) and the Client refuses it.
        var running = (await session.CallAsync(Cmd.Ping))["architecture"]?.GetValue<string>() ?? "";
        if (ClientUpdateArchitecture.Mismatch(PeArchitecture.Read(options.ClientFile), running))
            throw new InvalidOperationException(ClientUpdateArchitecture.ExplainOne(PeArchitecture.Read(options.ClientFile), running));
        var remote = StagingNamespace.RemotePath(settings, @"update\ClassroomClient.exe");
        await session.UploadAsync(options.ClientFile, remote, null);
        await session.CallAsync(Cmd.SelfUpdate, new JsonObject { ["path"] = remote, ["sha256"] = CliUpdate.Sha256(options.ClientFile), ["force"] = options.Force });
        return "update sent; the client service is restarting";
    }
}
