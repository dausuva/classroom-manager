// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.BrowserInventory;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LiveBrowserInventoryFactAttribute : FactAttribute
{
    public LiveBrowserInventoryFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_BROWSER_TEST_ADDRESS")))
            Skip = "Opt-in read-only browser inventory; requires explicit installed target and settings path.";
    }
}

[Collection(nameof(HostBackupTests))]
public sealed class LiveBrowserInventoryModuleTests(ITestOutputHelper output)
{
    [LiveBrowserInventoryFact]
    public async Task Selected_installed_client_reads_through_the_typed_browser_module_without_settings_changes()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_BROWSER_TEST_ADDRESS")!;
        var settingsPath = Environment.GetEnvironmentVariable("CLASSROOM_BROWSER_TEST_SETTINGS");
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
            using (var session = await ClientSession.OpenAsync(client, settings, deadline.Token))
            {
                var ping = await session.CallAsync(Cmd.Ping, ct: deadline.Token);
                client.Version = ping["version"]?.GetValue<string>() ?? "";
                client.User = ping["userName"]?.GetValue<string>() ?? "";
            }
            Assert.True(client.SupportsCommand(Cmd.BrowserList));
            var target = new BrowserInventoryTarget(0, client.Name, client.User.Length > 0,
                client.SupportsCommand(Cmd.BrowserList), client.SupportsCommand(Cmd.BrowserProfilesList));
            var commands = new List<string>();
            var loader = new BrowserInventoryLoader(async (_, cancellation) =>
                new ReadOnlyConnection(await ClientSession.OpenAsync(client, settings, cancellation), commands));
            var answers = await loader.ReadAsync([target], deadline.Token);
            var answer = Assert.Single(answers);
            Assert.Equal(InventoryReadStatus.Success, answer.Policies.Status);
            Assert.Equal(InventoryReadStatus.Success, answer.Browsers.Status);
            Assert.Equal(target.HasUser ? InventoryReadStatus.Success : InventoryReadStatus.NoUser, answer.Profiles.Status);
            var report = BrowserInventorySummary.Project(answers);
            Assert.Equal(1, report.Total);
            Assert.Equal(1, report.BrowsersRead);
            Assert.Equal(1, report.PoliciesRead);
            Assert.NotEmpty(BrowserInventorySummary.Describe(report));
            Assert.NotEmpty(BrowserInventorySummary.Profiles(report));
            Assert.Contains(Cmd.BrowserList, commands);
            Assert.DoesNotContain(Cmd.BrowserProfilesRemove, commands);
            // Counts/statuses only: no account, profile name, path, or raw wire response in test output.
            output.WriteLine($"Installed browser module: {client.Name}; protocol={client.Version}; browsers={answer.Browsers.Value!.Installed.Count}; " +
                $"policies={answer.Policies.Value!.Count}; profilesStatus={answer.Profiles.Status}; profiles={answer.Profiles.Value?.Count ?? 0}; calls={commands.Count}");
        }
        finally
        {
            HostSettings.SavesSuspended = suspended;
            Assert.Equal(before, File.ReadAllBytes(settingsPath!));
            Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
        }
    }

    sealed class ReadOnlyConnection(ClientSession session, List<string> commands) : IBrowserInventoryConnection
    {
        public Task<JsonObject> ReadAsync(string level, string command, CancellationToken cancellation)
        {
            Assert.Contains(command, new[] { Cmd.BrowserPolicyGet, Cmd.BrowserList, Cmd.BrowserProfilesList });
            commands.Add(command);
            return session.CallAtAsync(level, command, ct: cancellation);
        }
        public void Dispose() => session.Dispose();
    }
}
