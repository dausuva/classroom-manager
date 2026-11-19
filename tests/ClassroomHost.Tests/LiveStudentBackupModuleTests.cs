// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LiveStudentBackupFactAttribute : FactAttribute
{
    public LiveStudentBackupFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_BACKUP_TEST_ADDRESS")))
            Skip = "Opt-in read-only backup refusal probe; explicit enrolled device/settings required.";
    }
}

/// <summary>T-222: deliberately absent synthetic source; no real student data, copies or checkpoints.</summary>
[Collection(nameof(HostBackupTests))]
public sealed class LiveStudentBackupModuleTests(ITestOutputHelper output)
{
    [LiveStudentBackupFact]
    public async Task Installed_client_legacy_or_missing_source_inventory_cannot_copy_or_commit()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_BACKUP_TEST_ADDRESS")!;
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
            var saved = Assert.Single(settings.Computers, computer => computer.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            var client = new ClientInfo { Address = saved.Address, Port = saved.Port, ClientId = saved.ClientId, Mac = saved.Mac,
                KeyId = saved.KeyId, ComputerName = saved.Name, Alias = saved.Alias };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using (var pingSession = await ClientSession.OpenAsync(client, settings, deadline.Token))
            {
                var ping = await pingSession.CallAsync(Cmd.Ping, ct: deadline.Token);
                client.Version = ping["version"]?.GetValue<string>() ?? "";
                client.User = ping["userName"]?.GetValue<string>() ?? "";
            }
            Assert.False(string.IsNullOrWhiteSpace(client.User));
            var query = new RecentQuery { Folders = [@"C:\ClassroomManager-AbsentBackupProbe-" + Guid.NewGuid().ToString("N")],
                SinceUtc = StudentBackup.Never, AllFiles = true, MaxItems = 1 };
            var copied = false;
            var committed = false;
            RecentScan? observed = null;
            using var session = await ClientSession.OpenAsync(client, settings, deadline.Token);
            var failure = await Record.ExceptionAsync(() => StudentBackup.RunWorkflowAsync(query, async (page, cancellation) =>
            {
                observed = await RecentFilesCopier.ListAsync(session, page, cancellation);
                return observed;
            }, (_, _) => { copied = true; throw new Exception("Must not copy an incomplete source."); },
                _ => committed = true, TimeProvider.System, deadline.Token));
            Assert.False(copied);
            Assert.False(committed);
            if (Version.Parse(client.Version) < new Version(1, 19))
            {
                var unsupported = Assert.IsType<UnsupportedFeatureException>(failure);
                Assert.Contains("1.19", unsupported.Message);
                Assert.Null(observed);
                output.WriteLine($"Installed backup refusal probe: {client.Name}; protocol={client.Version}; " +
                    "fs.recent unsupported; copy=0; checkpoint=0.");
                return;
            }
            var error = Assert.IsType<IOException>(failure);
            Assert.NotNull(observed);
            Assert.Empty(observed.Items);
            Assert.NotEmpty(observed.Missing);
            Assert.StartsWith(observed.SupportsPaging ? "Backup incomplete: " : "Update the Client before backing up:", error.Message, StringComparison.Ordinal);
            output.WriteLine($"Installed backup refusal probe: {client.Name}; protocol={client.Version}; paging={observed.SupportsPaging}; " +
                $"synthetic missing sources={observed.Missing.Count}; copy=0; checkpoint=0.");
        }
        finally
        {
            HostSettings.SavesSuspended = suspended;
            Assert.Equal(before, File.ReadAllBytes(settingsPath!));
            Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
        }
    }
}
