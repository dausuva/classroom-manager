// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LiveResourceMeasurementFactAttribute : FactAttribute
{
    public LiveResourceMeasurementFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_MEASURE_ADDRESS")))
            Skip = "Opt-in installed resource/latency/throughput measurement; uses only generated files in a unique staging folder.";
    }
}

/// <summary>
/// T-161: samples the installed Client service and user helper from a background sampler on the target PC
/// while this Host runs idle, repeated-command, transfer, concurrent-transfer and excess-connection phases.
/// Raw samples go to CLASSROOM_MEASURE_OUT; the target's student files and settings are never touched.
/// </summary>
[Collection(nameof(HostBackupTests))]
public sealed class LiveResourceMeasurementTests(ITestOutputHelper output)
{
    const string Sampler = """
        param([string]$Out, [string]$Stop, [int]$Max)
        $ErrorActionPreference = 'Continue'
        $end = (Get-Date).AddSeconds($Max)
        Set-Content -LiteralPath $Out -Value 'utcMs,pid,role,workingSet,privateBytes,handles,threads,cpuSeconds' -Encoding ASCII
        $servicePid = 0
        $tick = 0
        while ((Get-Date) -lt $end -and -not (Test-Path -LiteralPath $Stop)) {
            if ($tick % 10 -eq 0) {
                $servicePid = [int](Get-CimInstance Win32_Service -Filter "Name='ClassroomManagerClient'").ProcessId
            }
            $tick++
            $now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            $lines = foreach ($p in Get-Process -Name ClassroomClient -ErrorAction SilentlyContinue) {
                $role = if ($p.Id -eq $servicePid) { 'service' } else { 'helper' }
                '{0},{1},{2},{3},{4},{5},{6},{7}' -f $now, $p.Id, $role, $p.WorkingSet64, $p.PrivateMemorySize64, $p.HandleCount, $p.Threads.Count, [math]::Round($p.TotalProcessorTime.TotalSeconds, 3).ToString([Globalization.CultureInfo]::InvariantCulture)
            }
            if ($lines) { Add-Content -LiteralPath $Out -Value $lines -Encoding ASCII }
            Start-Sleep -Seconds 1
        }
        """;

    const string Identity = """
        $ProgressPreference = 'SilentlyContinue'
        $svc = Get-CimInstance Win32_Service -Filter "Name='ClassroomManagerClient'"
        $exe = ($svc.PathName -split ' --service')[0].Trim('"')
        $os = Get-CimInstance Win32_OperatingSystem
        $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
        $sig = Get-AuthenticodeSignature -LiteralPath $exe
        $battery = @(Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue)
        [ordered]@{
            machine = $env:COMPUTERNAME; os = $os.Caption; build = $os.BuildNumber; architecture = $os.OSArchitecture
            cpu = $cpu.Name; logicalProcessors = [int]$env:NUMBER_OF_PROCESSORS
            ramGiB = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
            powerScheme = ((powercfg /getactivescheme) -join ' ')
            onBattery = ($battery.Count -gt 0 -and $battery[0].BatteryStatus -eq 1)
            clientVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
            clientSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
            signature = [string]$sig.Status
            serviceState = [string]$svc.State; servicePid = [int]$svc.ProcessId
            clientProcesses = @(Get-CimInstance Win32_Process -Filter "Name='ClassroomClient.exe'" | ForEach-Object { [ordered]@{ pid = $_.ProcessId; session = $_.SessionId; service = ($_.CommandLine -match '--service') } })
            freeGiB = [math]::Round((Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'").FreeSpace / 1GB, 1)
        } | ConvertTo-Json -Compress -Depth 5
        """;

    sealed record Phase(string Name, int Repetition, long StartMs, long EndMs);

    static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var v) ? v : fallback;

    [LiveResourceMeasurementFact]
    public async Task Installed_client_resources_latency_and_throughput_are_measured()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_MEASURE_ADDRESS")!;
        var settingsPath = Environment.GetEnvironmentVariable("CLASSROOM_MEASURE_SETTINGS");
        var outDir = Environment.GetEnvironmentVariable("CLASSROOM_MEASURE_OUT");
        Assert.False(string.IsNullOrWhiteSpace(settingsPath));
        Assert.False(string.IsNullOrWhiteSpace(outDir));
        int warmup = Env("CLASSROOM_MEASURE_WARMUP_SECONDS", 120), idle = Env("CLASSROOM_MEASURE_IDLE_SECONDS", 300),
            cooldown = Env("CLASSROOM_MEASURE_COOLDOWN_SECONDS", 300), repetitions = Env("CLASSROOM_MEASURE_REPETITIONS", 3),
            payloadMiB = Env("CLASSROOM_MEASURE_PAYLOAD_MIB", 16), concurrency = Env("CLASSROOM_MEASURE_CONCURRENCY", 4),
            pings = Env("CLASSROOM_MEASURE_PINGS", 200), excess = Env("CLASSROOM_MEASURE_EXCESS_CONNECTIONS", 12),
            excessBursts = Env("CLASSROOM_MEASURE_EXCESS_BURSTS", 1), excessHold = Env("CLASSROOM_MEASURE_EXCESS_HOLD_SECONDS", 60);

        var before = File.ReadAllBytes(settingsPath!);
        var keysPath = Path.ChangeExtension(settingsPath!, ".keys");
        var keysBefore = File.ReadAllBytes(keysPath);
        var suspended = HostSettings.SavesSuspended;
        HostSettings.SavesSuspended = true;
        Directory.CreateDirectory(outDir!);
        var local = Directory.CreateTempSubdirectory("cm-measure-").FullName;
        try
        {
            var settings = HostSettings.Load(settingsPath);
            var saved = Assert.Single(settings.Computers, c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            var client = new ClientInfo { Address = saved.Address, Port = saved.Port, ClientId = saved.ClientId,
                Mac = saved.Mac, KeyId = saved.KeyId, ComputerName = saved.Name };
            Task<ClientSession> Open(CancellationToken ct = default) => ClientSession.OpenAsync(client, settings, ct);

            var folder = StagingNamespace.RemotePath(settings, "measure-" + Guid.NewGuid().ToString("N"));
            var samplerScript = Path.Combine(folder, "Sampler.ps1");
            var samplesFile = Path.Combine(folder, "samples.csv");
            var stopFile = Path.Combine(folder, "stop.flag");
            var phases = new List<Phase>();
            var latency = new JsonObject();
            var transfers = new JsonArray();
            var excessResult = new JsonObject();
            var total = warmup + idle + cooldown + repetitions * 600 + excessBursts * (excessHold + 120) + 900;

            var control = await Open();
            async Task Renew() { control.Dispose(); control = await Open(); }
            var ping = await control.CallAsync(Cmd.Ping);
            Assert.Equal(saved.ClientId, ping["clientId"]?.GetValue<string>());
            client.Version = ping["version"]?.GetValue<string>() ?? "";
            var identity = JsonNode.Parse((await Command(control, Identity))["output"]!.GetValue<string>().Trim())!.AsObject();
            output.WriteLine("Identity: " + identity.ToJsonString());

            // A sampler left by an aborted earlier run would distort this one; remove it and its folder first.
            await Command(control, "Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | Where-Object { $_.CommandLine -like '*measure-*Sampler.ps1*' } | " +
                "ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; " +
                $"Get-ChildItem -LiteralPath '{Path.GetDirectoryName(folder)}' -Directory -Filter 'measure-*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force");
            var scriptBytes = Encoding.UTF8.GetBytes(Sampler);
            await control.CallAsync(Cmd.FsWrite, new JsonObject { ["path"] = samplerScript, ["offset"] = 0,
                ["data"] = Convert.ToBase64String(scriptBytes), ["total"] = scriptBytes.Length, ["complete"] = true });
            var launch = await Command(control, "$ProgressPreference = 'SilentlyContinue'; $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = " +
                $"'powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{samplerScript}\" -Out \"{samplesFile}\" -Stop \"{stopFile}\" -Max {total}' }}; $r.ReturnValue");
            Assert.Contains(launch["output"]!.GetValue<string>().Split('\n'), line => line.Trim() == "0");

            long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var failures = new JsonArray();
            // A failed phase is recorded and the run goes on: the failure is a result, not a reason to lose the samples.
            async Task Timed(string name, int rep, Func<Task> work)
            {
                var start = Now();
                try { await work(); }
                catch (Exception ex)
                {
                    failures.Add(new JsonObject { ["phase"] = name, ["repetition"] = rep, ["type"] = ex.GetType().Name, ["message"] = ex.Message });
                    output.WriteLine($"Phase {name} #{rep} FAILED: {ex.GetType().Name}: {ex.Message}");
                }
                phases.Add(new Phase(name, rep, start, Now()));
                output.WriteLine($"Phase {name} #{rep}: {(Now() - start) / 1000.0:F1} s");
            }

            try
            {
                await Timed("Warmup", 0, () => Task.Delay(TimeSpan.FromSeconds(warmup)));
                await Timed("Idle", 0, () => Task.Delay(TimeSpan.FromSeconds(idle)));

                var payload = new byte[payloadMiB * 1024 * 1024];
                RandomNumberGenerator.Fill(payload);
                var payloadHash = SHA256.HashData(payload);
                var payloadFile = Path.Combine(local, "payload.bin");
                await File.WriteAllBytesAsync(payloadFile, payload);

                var pingMs = new List<double>();
                var listMs = new List<double>();
                var commandMs = new List<double>();
                var connectMs = new List<double>();
                var uploadSeconds = new List<double>();
                var downloadSeconds = new List<double>();

                for (var rep = 1; rep <= repetitions; rep++)
                {
                    await Renew();   // the control connection idles through long phases; start each repetition on a live one
                    await Timed("RepeatedOperations", rep, async () =>
                    {
                        for (var i = 0; i < pings; i++) pingMs.Add(await Time(() => control.CallAsync(Cmd.Ping)));
                        for (var i = 0; i < 20; i++) listMs.Add(await Time(() => control.CallAsync(Cmd.FsList, new JsonObject { ["path"] = folder })));
                        for (var i = 0; i < 5; i++) commandMs.Add(await Time(() => Command(control, "$env:COMPUTERNAME")));
                        for (var i = 0; i < 10; i++)
                            connectMs.Add(await Time(async () => { using var fresh = await Open(); await fresh.CallAsync(Cmd.Ping); }));
                    });

                    await Renew();
                    await Timed("Transfer", rep, async () =>
                    {
                        var remote = Path.Combine(folder, $"single-{rep}.bin");
                        var sw = Stopwatch.StartNew();
                        await control.UploadAsync(payloadFile, remote);
                        var up = sw.Elapsed.TotalSeconds;
                        sw.Restart();
                        var back = Path.Combine(local, $"single-{rep}.bin");
                        await control.DownloadAsync(remote, back);
                        var down = sw.Elapsed.TotalSeconds;
                        Assert.Equal(payloadHash, SHA256.HashData(await File.ReadAllBytesAsync(back)));
                        File.Delete(back);
                        uploadSeconds.Add(up);
                        downloadSeconds.Add(down);
                    });

                    await Timed("ConcurrentTransfers", rep, async () =>
                    {
                        var sw = Stopwatch.StartNew();
                        var results = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async n =>
                        {
                            try
                            {
                                using var s = await Open();
                                var remote = Path.Combine(folder, $"parallel-{rep}-{n}.bin");
                                await s.UploadAsync(payloadFile, remote);
                                var back = Path.Combine(local, $"parallel-{rep}-{n}.bin");
                                await s.DownloadAsync(remote, back);
                                var readback = SHA256.HashData(await File.ReadAllBytesAsync(back));
                                File.Delete(back);
                                return payloadHash.SequenceEqual(readback) ? "ok" : "content mismatch";
                            }
                            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
                        }));
                        var seconds = sw.Elapsed.TotalSeconds;
                        var succeeded = results.Count(r => r == "ok");
                        transfers.Add(new JsonObject
                        {
                            ["repetition"] = rep, ["concurrency"] = concurrency, ["payloadMiBEach"] = payloadMiB,
                            ["completed"] = succeeded, ["wallSeconds"] = Math.Round(seconds, 2),
                            ["acknowledgedMiBPerSecond"] = Math.Round(2.0 * succeeded * payloadMiB / seconds, 2),
                            ["errors"] = new JsonArray(results.Where(r => r != "ok").Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                        });
                    });
                }

                latency["pingMs"] = Percentiles(pingMs);
                latency["fsListMs"] = Percentiles(listMs);
                latency["cmdRunMs"] = Percentiles(commandMs);
                latency["newConnectionPlusPingMs"] = Percentiles(connectMs);
                latency["singleUploadMiBPerSecond"] = Percentiles(uploadSeconds.Select(s => payloadMiB / s));
                latency["singleDownloadMiBPerSecond"] = Percentiles(downloadSeconds.Select(s => payloadMiB / s));
                latency["payloadMiB"] = payloadMiB;

                // Repeated bursts above the per-key cap: each excess session must already be refused and closed by the Client when its first
                // request is sent after a short hold, an admitted session must stay usable, and the sampler's per-burst handle and memory
                // figures show whether repeated attempts accumulate anything.
                var bursts = new JsonArray();
                for (var burst = 1; burst <= excessBursts; burst++)
                {
                    await Timed("ExcessConnections", burst, async () =>
                    {
                        var held = new List<ClientSession>();
                        var refusedAtOpen = 0;
                        var admitted = 0;
                        var refusedAfterHold = 0;
                        var probeMs = new List<double>();
                        try
                        {
                            for (var i = 0; i < excess; i++)
                            {
                                try { held.Add(await Open(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token)); }
                                catch (Exception) { refusedAtOpen++; }
                            }
                            await Task.Delay(TimeSpan.FromSeconds(excessHold));
                            var probes = held.Select(async s =>
                            {
                                var watch = Stopwatch.StartNew();
                                try { await s.CallAsync(Cmd.Ping, ct: new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token); return (Ok: true, Ms: watch.Elapsed.TotalMilliseconds); }
                                catch (Exception) { return (Ok: false, Ms: watch.Elapsed.TotalMilliseconds); }
                            }).ToList();
                            foreach (var result in await Task.WhenAll(probes))
                            {
                                if (result.Ok) admitted++; else refusedAfterHold++;
                                probeMs.Add(result.Ms);
                            }
                        }
                        finally { foreach (var s in held) s.Dispose(); }
                        // At the per-key cap a new connection is refused while the others are held; once they are closed it must be served again.
                        await Task.Delay(TimeSpan.FromSeconds(3));
                        var fresh = false;
                        try { using var again = await Open(); await again.CallAsync(Cmd.Ping); fresh = true; }
                        catch (Exception) { /* recorded as false */ }
                        bursts.Add(new JsonObject
                        {
                            ["burst"] = burst, ["requested"] = excess, ["refusedAtOpen"] = refusedAtOpen, ["opened"] = held.Count,
                            ["holdSeconds"] = excessHold, ["admittedAfterHold"] = admitted, ["refusedOrClosedAfterHold"] = refusedAfterHold,
                            ["firstRequestMsP50"] = Percentiles(probeMs)["p50"]?.DeepClone(), ["firstRequestMsMax"] = Percentiles(probeMs)["max"]?.DeepClone(),
                            ["freshConnectionStillServed"] = fresh,
                        });
                    });
                    // The pre-authentication bucket (40 new connections per address, 5 earned back per second) must be full again, or the next
                    // burst measures the Client's own connection back-off and the Host's key-refusal pause instead of the session cap.
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
                excessResult["requested"] = excess; excessResult["bursts"] = bursts;

                await Timed("Cooldown", 0, () => Task.Delay(TimeSpan.FromSeconds(cooldown)));
            }
            finally
            {
                control.Dispose();
                using var cleanup = await Open();
                await cleanup.CallAsync(Cmd.FsWrite, new JsonObject { ["path"] = stopFile, ["offset"] = 0,
                    ["data"] = "AA==", ["total"] = 1, ["complete"] = true });
                await Task.Delay(TimeSpan.FromSeconds(3));
                await cleanup.DownloadAsync(samplesFile, Path.Combine(outDir!, "samples.csv"));
                var tail = await Command(cleanup, "Get-ChildItem -LiteralPath (Join-Path $env:ProgramData 'ClassroomManager') -Recurse -Filter *.log -File -ErrorAction SilentlyContinue | " +
                    "Sort-Object LastWriteTime -Descending | Select-Object -First 1 | ForEach-Object { $_.FullName; Get-Content -LiteralPath $_.FullName -Tail 200 }");
                await File.WriteAllTextAsync(Path.Combine(outDir!, "client-log-tail.txt"), tail["output"]!.GetValue<string>());
                await cleanup.CallAsync(Cmd.FsDelete, new JsonObject { ["path"] = folder });
                output.WriteLine("Unique remote measurement folder removed.");
            }

            var summary = new JsonObject
            {
                ["address"] = address, ["capturedUtc"] = DateTime.UtcNow.ToString("o"), ["identity"] = identity,
                ["workload"] = new JsonObject { ["warmupSeconds"] = warmup, ["idleSeconds"] = idle, ["cooldownSeconds"] = cooldown,
                    ["repetitions"] = repetitions, ["pingsPerRepetition"] = pings, ["concurrency"] = concurrency, ["excessConnections"] = excess, ["excessBursts"] = excessBursts },
                ["phases"] = JsonSerializer.SerializeToNode(phases),
                ["latency"] = latency, ["concurrentTransfers"] = transfers, ["excessConnections"] = excessResult, ["failures"] = failures,
                ["resources"] = Resources(Path.Combine(outDir!, "samples.csv"), phases, identity["logicalProcessors"]!.GetValue<int>()),
            };
            await File.WriteAllTextAsync(Path.Combine(outDir!, "summary.json"),
                summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            HostSettings.SavesSuspended = suspended;
            Assert.Equal(before, File.ReadAllBytes(settingsPath!));
            Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
            Directory.Delete(local, true);
        }
    }

    static async Task<JsonObject> Command(ClientSession session, string script) =>
        await session.CallAtAsync(Levels.System, Cmd.CmdRun,
            new JsonObject { ["shell"] = "powershell", ["command"] = script, ["timeoutSec"] = 120 });

    static async Task<double> Time(Func<Task> work)
    {
        var sw = Stopwatch.StartNew();
        await work();
        return sw.Elapsed.TotalMilliseconds;
    }

    static JsonObject Percentiles(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return new JsonObject { ["count"] = 0 };
        double At(double p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
        return new JsonObject { ["count"] = sorted.Length, ["p50"] = Math.Round(At(0.5), 2), ["p95"] = Math.Round(At(0.95), 2),
            ["min"] = Math.Round(sorted[0], 2), ["max"] = Math.Round(sorted[^1], 2) };
    }

    static JsonObject Resources(string csv, List<Phase> phases, int logicalProcessors)
    {
        var rows = File.ReadAllLines(csv).Skip(1).Select(l => l.Split(',')).Where(p => p.Length == 8)
            .Select(p => (Ms: long.Parse(p[0], CultureInfo.InvariantCulture), Pid: p[1], Role: p[2],
                Ws: long.Parse(p[3], CultureInfo.InvariantCulture), Priv: long.Parse(p[4], CultureInfo.InvariantCulture),
                Handles: int.Parse(p[5], CultureInfo.InvariantCulture), Threads: int.Parse(p[6], CultureInfo.InvariantCulture),
                Cpu: double.Parse(p[7], CultureInfo.InvariantCulture))).ToList();
        var result = new JsonObject { ["samples"] = rows.Count };
        foreach (var role in rows.Select(r => r.Role).Distinct())
        {
            var perPhase = new JsonArray();
            foreach (var phase in phases)
            {
                var inPhase = rows.Where(r => r.Role == role && r.Ms >= phase.StartMs && r.Ms <= phase.EndMs).OrderBy(r => r.Ms).ToList();
                if (inPhase.Count == 0) { perPhase.Add(new JsonObject { ["phase"] = phase.Name, ["repetition"] = phase.Repetition, ["samples"] = 0 }); continue; }
                var wall = Math.Max(1, (inPhase[^1].Ms - inPhase[0].Ms) / 1000.0);
                var cpuSeconds = Math.Max(0, inPhase[^1].Cpu - inPhase[0].Cpu);
                perPhase.Add(new JsonObject
                {
                    ["phase"] = phase.Name, ["repetition"] = phase.Repetition, ["samples"] = inPhase.Count,
                    ["pids"] = string.Join('/', inPhase.Select(r => r.Pid).Distinct()),
                    ["privateMiB"] = Range(inPhase.Select(r => r.Priv / 1048576.0)),
                    ["workingSetMiB"] = Range(inPhase.Select(r => r.Ws / 1048576.0)),
                    ["handles"] = Range(inPhase.Select(r => (double)r.Handles)),
                    ["threads"] = Range(inPhase.Select(r => (double)r.Threads)),
                    ["cpuSeconds"] = Math.Round(cpuSeconds, 2),
                    ["cpuPercentOfAllProcessors"] = Math.Round(100 * cpuSeconds / wall / logicalProcessors, 3),
                });
            }
            result[role] = perPhase;
        }
        return result;
    }

    static JsonObject Range(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return new JsonObject { ["median"] = Math.Round(sorted[sorted.Length / 2], 1), ["min"] = Math.Round(sorted[0], 1), ["max"] = Math.Round(sorted[^1], 1) };
    }
}
