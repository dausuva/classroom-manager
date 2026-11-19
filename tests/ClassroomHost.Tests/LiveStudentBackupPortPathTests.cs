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

public sealed class LiveBackupPortPathFactAttribute : FactAttribute
{
    public LiveBackupPortPathFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_BACKUP_PORT_TEST_ADDRESS")))
            Skip = "Opt-in: runs the production backup (module ports) against one installed test Client; creates and deletes only a unique staging fixture.";
    }
}

/// <summary>
/// The production backup path, <see cref="StudentBackup.RunAsync"/>, against a real Client: the module asks through the ports, the Host copies
/// into a scratch folder. Unlike the round-trip test it does not use the workflow adapter, so it is the only test of the real
/// session, the real <see cref="ModuleRuns"/> connection and the broker together. Needs a signed-in user on the Client.
/// </summary>
[Collection(nameof(HostBackupTests))]
public sealed class LiveStudentBackupPortPathTests(ITestOutputHelper output)
{
    [LiveBackupPortPathFact]
    public async Task Production_backup_copies_through_the_module_ports_records_one_checkpoint_and_a_second_run_copies_nothing()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_BACKUP_PORT_TEST_ADDRESS")!;
        var settingsPath = Environment.GetEnvironmentVariable("CLASSROOM_BACKUP_TEST_SETTINGS");
        Assert.False(string.IsNullOrWhiteSpace(settingsPath));
        var before = File.ReadAllBytes(settingsPath!);
        var keysPath = Path.ChangeExtension(settingsPath!, ".keys");
        var keysBefore = File.ReadAllBytes(keysPath);
        var suspended = HostSettings.SavesSuspended;
        var local = Directory.CreateTempSubdirectory("cm-port-backup-").FullName;
        HostSettings.SavesSuspended = true;
        try
        {
            var settings = HostSettings.Load(settingsPath);
            var saved = Assert.Single(settings.Computers, c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            var client = new ClientInfo { Address = saved.Address, Port = saved.Port, ClientId = saved.ClientId,
                Mac = saved.Mac, KeyId = saved.KeyId, ComputerName = saved.Name };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var session = await ClientSession.OpenAsync(client, settings, deadline.Token);
            var ping = await session.CallAsync(Cmd.Ping, ct: deadline.Token);
            client.Version = ping["version"]?.GetValue<string>() ?? "";
            client.User = ping["userName"]?.GetValue<string>() ?? "";
            Assert.NotEmpty(client.User);
            output.WriteLine($"{client.Name}: client {client.Version}, protocol user {client.User}");

            var fixture = StagingNamespace.RemotePath(settings, "backup-port-" + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(fixture, "Source");
            var expected = new Dictionary<string, byte[]>
            {
                ["one.txt"] = Encoding.UTF8.GetBytes("first file\n" + new string('a', 3000)),
                ["two.txt"] = Encoding.UTF8.GetBytes("second file\n" + new string('b', 70_000)),
                [@"nested\three.txt"] = Encoding.UTF8.GetBytes("third file, in a folder\n" + new string('c', 500)),
            };
            settings.RecentFiles.WatchedFolders[RecentFilesPlan.ComputerKey(client)] = [source];
            settings.RecentFiles.Destination = Path.Combine(local, "Copies");
            var key = RecentFilesPlan.ComputerKey(client);
            try
            {
                foreach (var (name, bytes) in expected) await Write(Path.Combine(source, name), bytes);
                Assert.Equal(StudentBackup.Never, StudentBackup.LastBackupUtc(key));

                var first = await StudentBackup.RunAsync(session, client, settings, deadline.Token);
                output.WriteLine("First run: " + first);
                Assert.Contains("3", first);
                var computerFolder = RecentFilesPlan.ComputerFolder(StudentBackup.Destination(settings.RecentFiles), client);
                var label = RecentFilesPlan.FolderLabel(source);
                foreach (var (name, bytes) in expected)
                    Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(computerFolder, label, name), deadline.Token));
                Assert.Empty(Directory.GetFiles(local, "*.cm-partial", SearchOption.AllDirectories));
                Assert.True(StudentBackup.LastBackupUtc(key) > StudentBackup.Never);

                var second = await StudentBackup.RunAsync(session, client, settings, deadline.Token);
                output.WriteLine("Second run: " + second);

                // A file changed after the checkpoint is the only one copied next time.
                await Task.Delay(1500, deadline.Token);
                var changed = Encoding.UTF8.GetBytes("changed after the first backup " + Guid.NewGuid());
                await Write(Path.Combine(source, "two.txt"), changed);
                var third = await StudentBackup.RunAsync(session, client, settings, deadline.Token);
                output.WriteLine("Third run: " + third);
                Assert.Equal(changed, await File.ReadAllBytesAsync(Path.Combine(computerFolder, label, "two.txt"), deadline.Token));
                Assert.Equal(expected["one.txt"], await File.ReadAllBytesAsync(Path.Combine(computerFolder, label, "one.txt"), deadline.Token));
            }
            finally
            {
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
