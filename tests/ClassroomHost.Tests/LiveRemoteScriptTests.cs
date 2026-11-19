// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LiveRemoteScriptFactAttribute : FactAttribute
{
    public LiveRemoteScriptFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_REMOTE_ADDRESS")))
            Skip = "Opt-in: runs one reviewed PowerShell script on one designated lab PC and saves its output.";
    }
}

/// <summary>
/// Evidence driver for designated lab PCs: optionally uploads files into the Client's staging folder, runs one
/// reviewed PowerShell script as the service (or the signed-in user) and writes the result to a file. Saved
/// Host settings and keys are compared byte for byte before and after, as in the other live tests.
/// CLASSROOM_REMOTE_ADDRESS, _SETTINGS, _SCRIPT (local .ps1), _OUT (result .json); optional _LEVEL (system|user),
/// _TIMEOUT_SECONDS and _UPLOADS ("local>staging-relative;local>staging-relative").
/// </summary>
[Collection(nameof(HostBackupTests))]
public sealed class LiveRemoteScriptTests(ITestOutputHelper output)
{
    [LiveRemoteScriptFact]
    public async Task Reviewed_script_runs_on_the_designated_lab_pc()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_REMOTE_ADDRESS")!;
        var settingsPath = Environment.GetEnvironmentVariable("CLASSROOM_REMOTE_SETTINGS");
        var scriptPath = Environment.GetEnvironmentVariable("CLASSROOM_REMOTE_SCRIPT");
        var outPath = Environment.GetEnvironmentVariable("CLASSROOM_REMOTE_OUT");
        Assert.False(string.IsNullOrWhiteSpace(settingsPath));
        Assert.True(File.Exists(scriptPath), "CLASSROOM_REMOTE_SCRIPT must name a local script.");
        Assert.False(string.IsNullOrWhiteSpace(outPath));
        var level = string.Equals(Environment.GetEnvironmentVariable("CLASSROOM_REMOTE_LEVEL"), "user", StringComparison.OrdinalIgnoreCase)
            ? Levels.User : Levels.System;
        var timeout = int.TryParse(Environment.GetEnvironmentVariable("CLASSROOM_REMOTE_TIMEOUT_SECONDS"), out var t) ? t : 300;

        var before = File.ReadAllBytes(settingsPath!);
        var keysPath = Path.ChangeExtension(settingsPath!, ".keys");
        var keysBefore = File.ReadAllBytes(keysPath);
        var suspended = HostSettings.SavesSuspended;
        HostSettings.SavesSuspended = true;
        try
        {
            var settings = HostSettings.Load(settingsPath);
            var saved = Assert.Single(settings.Computers, c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            var client = new ClientInfo { Address = saved.Address, Port = saved.Port, ClientId = saved.ClientId,
                Mac = saved.Mac, KeyId = saved.KeyId, ComputerName = saved.Name };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeout + 120));
            using var session = await ClientSession.OpenAsync(client, settings, deadline.Token);
            var ping = await session.CallAsync(Cmd.Ping, ct: deadline.Token);
            Assert.Equal(saved.ClientId, ping["clientId"]?.GetValue<string>());

            var uploads = new JsonArray();
            foreach (var pair in (Environment.GetEnvironmentVariable("CLASSROOM_REMOTE_UPLOADS") ?? "")
                         .Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('>', 2);
                Assert.Equal(2, parts.Length);
                var remote = StagingNamespace.RemotePath(settings, parts[1].Trim());
                await session.UploadAsync(parts[0].Trim(), remote, ct: deadline.Token);
                uploads.Add(remote);
            }

            var run = await session.CallAtAsync(level, Cmd.CmdRun, new JsonObject
            {
                ["shell"] = "powershell", ["command"] = "$ProgressPreference = 'SilentlyContinue'\n" + File.ReadAllText(scriptPath!),
                ["timeoutSec"] = timeout,
            }, deadline.Token);
            var result = new JsonObject { ["address"] = address, ["machine"] = ping["machineName"]?.GetValue<string>(),
                ["level"] = level, ["uploads"] = uploads, ["exitCode"] = run["exitCode"]?.DeepClone(),
                ["timedOut"] = run["timedOut"]?.DeepClone(), ["output"] = run["output"]?.DeepClone() };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath!))!);
            await File.WriteAllTextAsync(outPath!, result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            output.WriteLine($"{address}: exit={run["exitCode"]}; result saved to {outPath}");
        }
        finally
        {
            HostSettings.SavesSuspended = suspended;
            Assert.Equal(before, File.ReadAllBytes(settingsPath!));
            Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
        }
    }
}
