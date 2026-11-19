// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LiveReleaseProbeFactAttribute : FactAttribute
{
    public LiveReleaseProbeFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_RELEASE_PROBE_ADDRESS")))
            Skip = "Opt-in: reads (changes nothing) the installed Client's file version, release identity line and digest on one installed test PC.";
    }
}

/// <summary>Reads what is installed on one test Client (T-218): used between update steps to see which release is really there.</summary>
[Collection(nameof(HostBackupTests))]
public sealed class LiveReleaseProbeTests(ITestOutputHelper output)
{
    const string Probe = """
        $exe = Join-Path $env:ProgramFiles 'ClassroomClient\ClassroomClient.exe'
        $i = (Get-Item -LiteralPath $exe).VersionInfo
        "probe=file:$($i.FileVersion)|product:$($i.ProductVersion)|line:$($i.LegalTrademarks)|sha:$((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLower())"
        """;

    [LiveReleaseProbeFact]
    public async Task Report_the_installed_client_release()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_RELEASE_PROBE_ADDRESS")!;
        var settingsPath = Environment.GetEnvironmentVariable("CLASSROOM_BACKUP_TEST_SETTINGS");
        Assert.False(string.IsNullOrWhiteSpace(settingsPath));
        var before = File.ReadAllBytes(settingsPath!);
        var keysPath = Path.ChangeExtension(settingsPath!, ".keys");
        var keysBefore = File.ReadAllBytes(keysPath);
        var suspended = HostSettings.SavesSuspended;
        HostSettings.SavesSuspended = true;
        try
        {
            var settings = HostSettings.Load(settingsPath);
            var saved = Assert.Single(settings.Computers, c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            var client = new ClientInfo { Address = saved.Address, Port = saved.Port, ClientId = saved.ClientId, Mac = saved.Mac, KeyId = saved.KeyId, ComputerName = saved.Name };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var session = await ClientSession.OpenAsync(client, settings, deadline.Token);
            var data = await session.CallAtAsync(Levels.System, Cmd.CmdRun,
                new JsonObject { ["shell"] = "powershell", ["command"] = Probe, ["timeoutSec"] = 60 }, deadline.Token);
            var line = (data["output"]?.GetValue<string>() ?? "").Split((char)10).Select(item => item.Trim()).FirstOrDefault(item => item.StartsWith("probe=")) ?? "(no answer)";
            output.WriteLine($"{saved.Name} {line}");
        }
        finally
        {
            HostSettings.SavesSuspended = suspended;
            Assert.Equal(before, File.ReadAllBytes(settingsPath!));
            Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
        }
    }
}
