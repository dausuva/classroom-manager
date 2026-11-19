// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LiveInstallerRunFactAttribute : FactAttribute
{
    public LiveInstallerRunFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_INSTALLER_TEST_ADDRESS")))
            Skip = "Opt-in: runs the given installers, one after the other, silently on one installed test PC (as SYSTEM, through a one-time scheduled task), and reports the exit code and the installed release after each.";
    }
}

/// <summary>
/// The kit's own installers on a real PC (T-218): the same installer again must repair, a later release of the same public version must replace the
/// installed one although the file version is the same, and an older release must be refused. Reports only; the caller judges and restores the PC.
/// <list type="bullet">
/// <item>CLASSROOM_INSTALLER_TEST_ADDRESS, CLASSROOM_INSTALLER_TEST_FILES (installers, separated by semicolons), CLASSROOM_BACKUP_TEST_SETTINGS.</item>
/// <item>CLASSROOM_INSTALLER_TEST_PROBE_EXE: the program whose release is reported (the installed Client when left out).</item>
/// <item>CLASSROOM_INSTALLER_TEST_AFTER: a script and the files it needs, separated by semicolons; uploaded together and run once at the end as SYSTEM (an uninstall, say).</item>
/// </list>
/// </summary>
[Collection(nameof(HostBackupTests))]
public sealed class LiveInstallerRunTests(ITestOutputHelper output)
{
    const string Folder = @"C:\ProgramData\ClassroomManager\installer-test";

    static string ProbeScript(string exe) => $$"""
        $exe = '{{exe}}'
        if (-not (Test-Path -LiteralPath $exe)) { "probe=(not installed)" } else {
            $i = (Get-Item -LiteralPath $exe).VersionInfo
            "probe=file:$($i.FileVersion)|line:$($i.LegalTrademarks)|sha:$((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLower())"
        }
        """;

    [LiveInstallerRunFact]
    public async Task Run_the_installers_in_order_and_report_each_result()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_INSTALLER_TEST_ADDRESS")!;
        var files = (Environment.GetEnvironmentVariable("CLASSROOM_INSTALLER_TEST_FILES") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var after = (Environment.GetEnvironmentVariable("CLASSROOM_INSTALLER_TEST_AFTER") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var probeExe = Environment.GetEnvironmentVariable("CLASSROOM_INSTALLER_TEST_PROBE_EXE") is { Length: > 0 } given ? given : @"C:\Program Files\ClassroomClient\ClassroomClient.exe";
        Assert.True(files.Length > 0 || after.Length > 0, "give installers, a script to run, or both");
        var settingsPath = Environment.GetEnvironmentVariable("CLASSROOM_BACKUP_TEST_SETTINGS");
        Assert.False(string.IsNullOrWhiteSpace(settingsPath));
        foreach (var file in files.Concat(after)) Assert.True(File.Exists(file), file);
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

            async Task<JsonObject> RunAsync(string command, TimeSpan timeout)
            {
                using var deadline = new CancellationTokenSource(timeout);
                using var session = await ClientSession.OpenAsync(client, settings, deadline.Token);
                return await session.CallAtAsync(Levels.System, Cmd.CmdRun, new JsonObject { ["shell"] = "powershell", ["command"] = command, ["timeoutSec"] = (int)timeout.TotalSeconds }, deadline.Token);
            }
            async Task<string> ProbeAsync()
            {
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    try
                    {
                        var data = await RunAsync(ProbeScript(probeExe), TimeSpan.FromSeconds(60));
                        return (data["output"]?.GetValue<string>() ?? "").Split((char)10).Select(item => item.Trim()).FirstOrDefault(item => item.StartsWith("probe=")) ?? "(no answer)";
                    }
                    catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { await Task.Delay(TimeSpan.FromSeconds(10)); }   // the Client restarts during a Client install
                }
                return "(unreachable)";
            }
            async Task UploadAsync(string local, string relative)
            {
                using var upload = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                using var session = await ClientSession.OpenAsync(client, settings, upload.Token);
                await session.UploadAsync(local, StagingNamespace.RemotePath(settings, relative), null);
            }
            // A script run as SYSTEM in a one-time scheduled task, so that nothing it does to the Client service can end it. Returns its exit code.
            async Task<string?> RunDetachedAsync(string label, string scriptBody)
            {
                var task = "CmInstallerTest" + label;
                var escaped = scriptBody.Replace("'", "''");
                await RunAsync($"Set-Content -LiteralPath '{Folder}\\run-{label}.ps1' -Value '{escaped}'; Remove-Item -LiteralPath '{Folder}\\exit.txt' -ErrorAction SilentlyContinue; " +
                               $"schtasks /create /tn {task} /tr \"powershell -NoProfile -ExecutionPolicy Bypass -File '{Folder}\\run-{label}.ps1'\" /sc once /st 00:00 /ru SYSTEM /f | Out-Null; schtasks /run /tn {task} | Out-Null",
                    TimeSpan.FromSeconds(60));
                string? exit = null;
                var limit = DateTime.UtcNow.AddMinutes(8);
                while (DateTime.UtcNow < limit && exit is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    try
                    {
                        var data = await RunAsync($"if (Test-Path '{Folder}\\exit.txt') {{ 'exit=' + (Get-Content '{Folder}\\exit.txt' -Raw).Trim() }}", TimeSpan.FromSeconds(30));
                        exit = (data["output"]?.GetValue<string>() ?? "").Split((char)10).Select(item => item.Trim()).FirstOrDefault(item => item.StartsWith("exit="))?[5..];
                    }
                    catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { /* the service may be restarting */ }
                }
                await Task.Delay(TimeSpan.FromSeconds(15));
                try { await RunAsync($"schtasks /delete /tn {task} /f | Out-Null", TimeSpan.FromSeconds(60)); } catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { }
                return exit;
            }

            output.WriteLine($"{saved.Name} before: {await ProbeAsync()}");
            await RunAsync($"New-Item -ItemType Directory -Force -Path '{Folder}' | Out-Null", TimeSpan.FromSeconds(60));
            var step = 0;
            foreach (var file in files)
            {
                step++;
                var remote = StagingNamespace.RemotePath(settings, $@"installer-test\setup-{step}.exe");
                await UploadAsync(file, $@"installer-test\setup-{step}.exe");
                // Waits for the installer process only: Start-Process -Wait also waits for everything the installer starts, and the Host installer starts the teacher app.
                var runner = $"$p = Start-Process -FilePath '{remote}' -ArgumentList '/SP-','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LOG={Folder}\\setup.log' -PassThru; $p.WaitForExit(); Set-Content -LiteralPath '{Folder}\\exit.txt' -Value $p.ExitCode";
                var exit = await RunDetachedAsync(step.ToString(), runner);
                output.WriteLine($"step {step} {Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file)) ?? "")} installer exit={exit ?? "(none within 8 minutes)"} -> {await ProbeAsync()}");
            }
            if (after.Length > 0)
            {
                foreach (var file in after) await UploadAsync(file, $@"installer-test\after\{Path.GetFileName(file)}");
                var folder = StagingNamespace.RemotePath(settings, @"installer-test\after");
                var script = Path.GetFileName(after[0]);
                var runner = $"Set-Location '{folder}'; & powershell -NoProfile -ExecutionPolicy Bypass -File '{folder}\\{script}' *> '{Folder}\\after.log'; Set-Content -LiteralPath '{Folder}\\exit.txt' -Value $LASTEXITCODE";
                var exit = await RunDetachedAsync("after", runner);
                var log = await RunAsync($"if (Test-Path '{Folder}\\after.log') {{ Get-Content '{Folder}\\after.log' | Select-Object -Last 8 | ForEach-Object {{ 'after-log=' + $_ }} }}", TimeSpan.FromSeconds(60));
                output.WriteLine($"after-script exit={exit ?? "(none within 8 minutes)"} -> {await ProbeAsync()}\n{log["output"]?.GetValue<string>()}");
            }
            try { await RunAsync($"Remove-Item -LiteralPath '{Folder}' -Recurse -Force -ErrorAction SilentlyContinue", TimeSpan.FromSeconds(60)); } catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { }
        }
        finally
        {
            HostSettings.SavesSuspended = suspended;
            Assert.Equal(before, File.ReadAllBytes(settingsPath!));
            Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
        }
    }
}
