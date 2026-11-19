// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>
/// These tests point the Client's one shared log at a file of their own. Run alone: a test of another
/// class writing to the log meanwhile would hold that file open ("used by another process").
/// </summary>
[CollectionDefinition(nameof(SharedClientLog), DisableParallelization = true)]
public sealed class SharedClientLog { }

[Collection(nameof(SharedClientLog))]
public sealed class InstallerLogPrivacyTests
{
    [Fact]
    public async Task Client_log_masks_configured_keys_in_labels_and_never_falls_back_to_raw_text()
    {
        const string secret = "synthetic-configured-secret-6cd08";
        var path = Path.GetTempFileName();
        var previous = Log.FilePath;
        var provider = Log.KnownSecrets;
        try
        {
            Log.FilePath = path;
            Log.KnownSecrets = () => [secret];
            Log.Write("010002 account label " + secret);
            var before = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(secret, before);
            Assert.Contains("010002", before);
            Log.KnownSecrets = () => throw new InvalidOperationException(secret);
            Log.Write(secret);
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        }
        finally { Log.FilePath = previous; Log.KnownSecrets = provider; File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_import_log_omits_filename_and_failure_output(bool fails)
    {
        const string secret = "SYNTHETIC-REGISTRY-SECRET-c614";
        var root = Directory.CreateTempSubdirectory("registry-log-test-").FullName;
        var path = Path.Combine(root, secret + ".reg");
        await File.WriteAllTextAsync(path, "fixture only; never imported");
        var log = Path.Combine(root, "fixture.log");
        var previous = Log.FilePath;
        try
        {
            Log.FilePath = log;
            var handler = new RegistryHandler((_, _, _) => Task.FromResult(fails ? Response.Fail(secret) : Response.Success(new JsonObject { ["exitCode"] = 0 })), root);
            var result = await handler.ImportAsync(new JsonObject { ["path"] = path }, CancellationToken.None);
            Assert.Equal(!fails, result.Ok);
            var text = await File.ReadAllTextAsync(log);
            Assert.DoesNotContain(secret, text);
            Assert.Contains("registry.import: completed", text);
            Assert.Contains("exitCode=", text);
        }
        finally { Log.FilePath = previous; Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Server_log_omits_raw_failures_key_labels_and_unknown_command_text(bool unknown, bool throws)
    {
        const string secret = "SYNTHETIC-SERVER-ERROR-TOKEN-185e";
        const string key = "synthetic-server-log-key";
        var log = Path.GetTempFileName();
        var previous = Log.FilePath;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            Log.FilePath = log;
            var server = new CommandServer(new ClientConfig { UseTls = false },
                new KeyStore("synthetic-primary", [new ClientAccessKey { Name = secret, Role = "Teacher", Psk = key }]),
                (_, _, _, _) => throws ? throw new InvalidOperationException(secret) : Task.FromResult(Response.Fail(secret)));
            var serving = Task.Run(async () => await server.HandleAsync(await listener.AcceptTcpClientAsync(limit.Token), limit.Token));
            using (var session = await ClientSession.OpenWithKeyAsync(new ClientInfo
            { Address = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port }, new HostSettings { UseTls = false }, key, limit.Token))
            {
                var error = await Assert.ThrowsAsync<RemoteException>(() => session.CallAsync(unknown ? secret : Cmd.Ping, ct: limit.Token));
                Assert.Contains(secret, error.Message); // Error is returned to its authorized caller, not persisted.
            }
            await serving;
            var text = await File.ReadAllTextAsync(log);
            Assert.DoesNotContain(secret, text);
            Assert.Contains("ok=False", text);
            Assert.Contains(unknown ? "unknown" : Cmd.Ping, text);
            Assert.Contains(SharedKey.Fingerprint(key), text);
        }
        finally { listener.Stop(); Log.FilePath = previous; File.Delete(log); }
    }

    [Fact]
    public async Task Winget_log_omits_package_fields_and_raw_failure_output_without_running_winget()
    {
        const string secret = "SYNTHETIC-WINGET-TOKEN-59ac";
        var log = Path.GetTempFileName();
        var previous = Log.FilePath;
        try
        {
            Log.FilePath = log;
            var handler = new InstallHandler((psi, _, _) =>
            {
                Assert.Contains(secret, psi.Arguments);
                return Task.FromResult(new ClassroomManager.Shared.Response { Ok = false, Error = "synthetic error " + secret,
                    Data = new JsonObject { ["exitCode"] = 7, ["output"] = secret } });
            });
            var response = await handler.WingetAsync(new JsonObject
            { ["id"] = secret, ["action"] = secret, ["scope"] = secret }, CancellationToken.None);
            Assert.False(response.Ok);
            var text = await File.ReadAllTextAsync(log);
            Assert.DoesNotContain(secret, text);
            Assert.Contains("package details omitted", text);
            Assert.Contains("exitCode=7", text);
            Assert.Contains("action=install", text);
        }
        finally { Log.FilePath = previous; File.Delete(log); }
    }

    [Theory]
    [InlineData("cmd", 0)]
    [InlineData("cmd", 7)]
    [InlineData("powershell", 0)]
    [InlineData("SYNTHETIC-SHELL-TOKEN-75ad", 0)]
    public async Task Terminal_command_and_output_are_returned_but_never_written_to_product_log(string shell, int exitCode)
    {
        const string secret = "SYNTHETIC-COMMAND-PASSWORD-75ad";
        var log = Path.GetTempFileName();
        var previous = Log.FilePath;
        try
        {
            Log.FilePath = log;
            var command = shell == "powershell" ? $"Write-Output '{secret}'; exit {exitCode}"
                : $"echo {secret}& exit /b {exitCode}";
            var response = await new CommandHandler().RunAsync(new JsonObject
            { ["shell"] = shell, ["command"] = command, ["timeoutSec"] = 15 }, CancellationToken.None);
            Assert.True(response.Ok);
            Assert.Equal(exitCode, response.Data["exitCode"]!.GetValue<int>());
            Assert.Contains(secret, response.Data["output"]!.GetValue<string>());
            var text = await File.ReadAllTextAsync(log);
            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain("SYNTHETIC-SHELL-TOKEN", text);
            Assert.Contains("command omitted", text);
            Assert.Contains("output omitted", text);
            Assert.Contains($"exitCode={exitCode}", text);
            var ids = System.Text.RegularExpressions.Regex.Matches(text, @"cmd\.run \[([0-9a-f]{32})\]");
            Assert.Equal(2, ids.Count);
            Assert.Equal(ids[0].Groups[1].Value, ids[1].Groups[1].Value);
        }
        finally { Log.FilePath = previous; File.Delete(log); }
    }

    [Fact]
    public async Task Terminal_timeout_keeps_metadata_without_recording_the_command()
    {
        var log = Path.GetTempFileName();
        var previous = Log.FilePath;
        try
        {
            Log.FilePath = log;
            var response = await new CommandHandler().RunAsync(new JsonObject
            { ["shell"] = "powershell", ["command"] = "Start-Sleep 60; 'SYNTHETIC-TIMEOUT-TOKEN'", ["timeoutSec"] = 1 }, CancellationToken.None);
            Assert.True(response.Data["timedOut"]!.GetValue<bool>());
            Assert.Equal(-1, response.Data["exitCode"]!.GetValue<int>());
            var text = await File.ReadAllTextAsync(log);
            Assert.DoesNotContain("SYNTHETIC-TIMEOUT-TOKEN", text);
            Assert.Contains("timedOut=True", text);
        }
        finally { Log.FilePath = previous; File.Delete(log); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Installer_arguments_are_not_logged_on_success_or_nonzero_exit(int exitCode)
    {
        const string secret = "SYNTHETIC-INSTALLER-PASSWORD-84b9";
        var log = Path.GetTempFileName();
        var previous = Log.FilePath;
        try
        {
            Log.FilePath = log;
            // Exercise the real handler without installing anything or writing machine settings.
            var response = await new InstallHandler().RunInstallerAsync(new JsonObject
            {
                ["path"] = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                ["args"] = $"/d /c \"set CM_SYNTHETIC_TEST={secret}& exit /b {exitCode}\"",
                ["timeoutSec"] = 15
            }, CancellationToken.None);
            Assert.Equal(exitCode == 0, response.Ok);
            Assert.Equal(exitCode, response.Data["exitCode"]!.GetValue<int>());
            var text = await File.ReadAllTextAsync(log);
            Assert.DoesNotContain(secret, text);
            Assert.Contains("arguments omitted", text);
            Assert.Contains($"exitCode={exitCode}", text);
            var ids = System.Text.RegularExpressions.Regex.Matches(text, @"install\.run \[([0-9a-f]{32})\]");
            Assert.Equal(2, ids.Count);
            Assert.Equal(ids[0].Groups[1].Value, ids[1].Groups[1].Value);
        }
        finally { Log.FilePath = previous; File.Delete(log); }
    }

    [Fact]
    public async Task Process_start_failure_does_not_echo_arguments_or_private_installer_path()
    {
        const string secret = "SYNTHETIC-INSTALLER-TOKEN-782a";
        var folder = Directory.CreateTempSubdirectory("cm-installer-log-test-");
        var log = Path.Combine(folder.FullName, "client.log");
        var previous = Log.FilePath;
        var candidate = Path.Combine(folder.FullName, secret + ".exe");
        try
        {
            Log.FilePath = log;
            await File.WriteAllTextAsync(candidate, "not an executable");
            var response = await new InstallHandler().RunInstallerAsync(new JsonObject
            {
                ["path"] = candidate, ["args"] = $"PASSWORD={secret}", ["timeoutSec"] = 15
            }, CancellationToken.None);
            Assert.False(response.Ok);
            Assert.Contains("Could not start process", response.Error);
            Assert.DoesNotContain(secret, response.Error);
            var text = await File.ReadAllTextAsync(log);
            Assert.DoesNotContain(secret, text);
            Assert.Contains("ok=False; exitCode=unavailable", text);
        }
        finally
        {
            Log.FilePath = previous;
            File.Delete(candidate);
            File.Delete(log);
            folder.Delete();
        }
    }
}
