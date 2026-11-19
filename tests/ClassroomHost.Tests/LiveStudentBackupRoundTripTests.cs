// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LiveBackupRoundTripFactAttribute : FactAttribute
{
    public LiveBackupRoundTripFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_BACKUP_WRITE_TEST_ADDRESS")))
            Skip = "Opt-in installed backup round trip; creates/deletes only a unique staging fixture.";
    }
}

/// <summary>Exercises installed user scan, paged backup, isolated checkpoint and file restore without student data.</summary>
[Collection(nameof(HostBackupTests))]
public sealed class LiveStudentBackupRoundTripTests(ITestOutputHelper output)
{
    [LiveBackupRoundTripFact]
    public async Task Installed_client_pages_copies_checkpoints_and_restores_generated_files()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_BACKUP_WRITE_TEST_ADDRESS")!;
        var settingsPath = Environment.GetEnvironmentVariable("CLASSROOM_BACKUP_TEST_SETTINGS");
        Assert.False(string.IsNullOrWhiteSpace(settingsPath));
        var before = File.ReadAllBytes(settingsPath!);
        var keysPath = Path.ChangeExtension(settingsPath!, ".keys");
        var keysBefore = File.ReadAllBytes(keysPath);
        var suspended = HostSettings.SavesSuspended;
        var local = Directory.CreateTempSubdirectory("cm-installed-backup-").FullName;
        HostSettings.SavesSuspended = true;
        try
        {
            var settings = HostSettings.Load(settingsPath);
            var saved = Assert.Single(settings.Computers, c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(saved.ClientId);
            var client = new ClientInfo { Address = saved.Address, Port = saved.Port, ClientId = saved.ClientId,
                Mac = saved.Mac, KeyId = saved.KeyId, ComputerName = saved.Name };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var session = await ClientSession.OpenAsync(client, settings, deadline.Token);
            var ping = await session.CallAsync(Cmd.Ping, ct: deadline.Token);
            Assert.Equal(saved.ClientId, ping["clientId"]?.GetValue<string>());
            Assert.Equal(saved.Name, ping["machineName"]?.GetValue<string>(), ignoreCase: true);
            client.Version = ping["version"]?.GetValue<string>() ?? "";
            client.User = ping["userName"]?.GetValue<string>() ?? "";
            Assert.NotEmpty(client.User);
            var fixture = StagingNamespace.RemotePath(settings, "backup-acceptance-" + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(fixture, "Source");
            var expected = Enumerable.Range(0, 7).ToDictionary(i => $"fixture-{i}.txt",
                i => Encoding.UTF8.GetBytes($"Generated backup acceptance {i}\n" + new string((char)('a' + i), 2048)));
            var pages = 0;
            var commits = 0;
            var entries = new List<RecentEntry>();
            var checkpoint = Path.Combine(local, "isolated-checkpoint.json");
            var destination = Path.Combine(local, "Copies");
            try
            {
                foreach (var (name, bytes) in expected)
                    await Write(Path.Combine(source, name), bytes);
                var summary = await StudentBackup.RunWorkflowAsync(new RecentQuery
                {
                    Folders = [source], SinceUtc = StudentBackup.Never, AllFiles = true, MaxItems = 2
                }, async (query, ct) =>
                {
                    pages++;
                    var scan = await RecentFilesCopier.ListAsync(session, query, ct);
                    Assert.True(scan.SupportsPaging);
                    entries.AddRange(scan.Items);
                    return scan;
                }, (page, ct) => RecentFilesCopier.CopyAsync(session, client, page, destination, true, ct: ct),
                started => { commits++; StudentBackup.RecordBackup(client.ClientId, started, checkpoint); },
                TimeProvider.System, deadline.Token);
                Assert.Equal(4, pages);
                Assert.Equal(1, commits);
                Assert.Equal(7, entries.Count);
                Assert.True(StudentBackup.LastBackupUtc(client.ClientId, checkpoint) > StudentBackup.Never);
                foreach (var entry in entries)
                {
                    var name = Path.GetFileName(entry.Path);
                    var copied = RecentFilesPlan.LocalPath(destination, client, entry)!;
                    Assert.Equal(expected[name], await File.ReadAllBytesAsync(copied, deadline.Token));
                    var restored = Path.Combine(fixture, "Restored", name);
                    await session.UploadAsync(copied, restored, ct: deadline.Token);
                    var readback = Path.Combine(local, "readback-" + name);
                    await session.DownloadAsync(restored, readback, ct: deadline.Token);
                    Assert.Equal(expected[name], await File.ReadAllBytesAsync(readback, deadline.Token));
                }
                output.WriteLine($"Installed backup round trip: {client.Name}; protocol={client.Version}; pages={pages}; " +
                    $"copied=7; restored=7; byte comparisons=14; isolated checkpoints={commits}; {summary}");
            }
            finally
            {
                // A fresh connection makes fixture cleanup possible even if a prior test call failed.
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var cleanup = await ClientSession.OpenAsync(client, settings, cleanupDeadline.Token);
                await cleanup.CallAsync(Cmd.FsDelete, new JsonObject { ["path"] = fixture }, cleanupDeadline.Token);
                output.WriteLine("Unique remote staging fixture removed.");
            }

            async Task Write(string path, byte[] bytes) => await session.CallAsync(Cmd.FsWrite,
                new JsonObject { ["path"] = path, ["offset"] = 0, ["data"] = Convert.ToBase64String(bytes),
                    ["total"] = bytes.Length, ["complete"] = true }, deadline.Token);
        }
        finally
        {
            HostSettings.SavesSuspended = suspended;
            Assert.Equal(before, File.ReadAllBytes(settingsPath!));
            Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
            Directory.Delete(local, true);
        }
    }
}
