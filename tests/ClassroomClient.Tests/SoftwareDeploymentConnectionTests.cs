// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

// T-222 MW-B: production session/adapter/server with synthetic handlers only. Never runs an installer or registry import.
public sealed class SoftwareDeploymentConnectionTests
{
    const string Key = "synthetic-software-deployment-key";
    sealed class LocalFile : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "software-deployment-" + Guid.NewGuid().ToString("N"));
        public string PathName => Path.Combine(_root, "fixture.exe");
        public LocalFile() { Directory.CreateDirectory(_root); File.WriteAllBytes(PathName, [1, 2, 3]); }
        public void Dispose() { File.Delete(PathName); Directory.Delete(_root); }
    }

    sealed class Rig : IAsyncDisposable
    {
        readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly List<Task> _connections = [];
        readonly Task _accepting;
        public readonly ConcurrentQueue<(string Level, string Command, JsonObject Args)> Calls = new();
        public CancellationToken Token => _stop.Token;
        public ClientInfo Client => new() { Address = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port, Version = Protocol.Version, User = "fixture" };
        public Rig(bool reuse = false, params string[] refused)
        {
            var server = new CommandServer(new ClientConfig { UseTls = false }, new KeyStore(Key, []), (request, _, _, _) =>
            {
                Calls.Enqueue((request.Level, request.Cmd, request.Args?.DeepClone().AsObject() ?? new()));
                var reply = refused.Contains(request.Cmd) ? Response.Fail("fixture refusal: " + request.Cmd)
                    : request.Cmd == Cmd.FsList ? Response.Success(new JsonObject
                    { ["entries"] = new JsonArray(new JsonObject { ["name"] = "fixture.exe", ["size"] = reuse ? 3L : 0L, ["isDir"] = false }) })
                    : Response.Success(new JsonObject { ["exitCode"] = 3010 });
                return Task.FromResult(reply);
            }, new PreAuthGate(new ConnectionLimits { FailuresBeforeBackoff = 100 }));
            _listener.Start();
            _accepting = Task.Run(async () =>
            {
                try
                {
                    while (!Token.IsCancellationRequested)
                    {
                        var tcp = await _listener.AcceptTcpClientAsync(Token);
                        lock (_connections) _connections.Add(server.HandleAsync(tcp, Token));
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _accepting;
            _listener.Stop();
            Task[] connections;
            lock (_connections) connections = [.. _connections];
            await Task.WhenAll(connections);
            _stop.Dispose();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Uploaded_installer_reuses_exact_size_preserves_run_arguments_and_retains_failed_installs(bool reuse, bool fail)
    {
        using var file = new LocalFile();
        await using var rig = new Rig(reuse, fail ? Cmd.InstallRun : "");
        var client = rig.Client;
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        var log = new List<string>();
        var task = new ClientSoftwareDeployment(client, session, @"C:\fixture-staging", log.Add)
            .DeployAsync(new(SoftwareAction.Exe, Levels.User, file.PathName, "", "/quiet synthetic-option", "", "default"));
        if (fail) await Assert.ThrowsAsync<RemoteException>(() => task); else await task;
        var expected = new List<string> { Cmd.FsList };
        if (!reuse) expected.Add(Cmd.FsWrite);
        expected.Add(Cmd.InstallRun);
        Assert.Equal(expected, rig.Calls.Select(call => call.Command));
        var run = Assert.Single(rig.Calls, call => call.Command == Cmd.InstallRun);
        Assert.Equal(Levels.User, run.Level);
        Assert.Equal(@"C:\fixture-staging\install\fixture.exe", run.Args["path"]!.GetValue<string>());
        Assert.Equal("/quiet synthetic-option", run.Args["args"]!.GetValue<string>());
        Assert.Equal(1800, run.Args["timeoutSec"]!.GetValue<int>());
        Assert.DoesNotContain(log, line => line.Contains("synthetic-option"));
        Assert.Equal(!fail, log.Any(line => line.Contains("with code 3010")));
        if (!reuse)
        {
            var write = Assert.Single(rig.Calls, call => call.Command == Cmd.FsWrite);
            Assert.Equal(Levels.System, write.Level);
            Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(write.Args["data"]!.GetValue<string>()));
        }
        await session.CallAsync(Cmd.Ping, ct: rig.Token); // Adapter does not dispose its caller-owned session.
    }

    [Fact]
    public async Task Normal_install_reuses_temporary_upload_location_and_keeps_it_for_the_next_run()
    {
        using var file = new LocalFile();
        await using var rig = new Rig(reuse: true);
        var client = rig.Client;
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        var settings = new HostSettings();
        var staging = StagingNamespace.Root(settings);
        var remote = StagingNamespace.RemotePath(settings, @"install\fixture.exe");
        Assert.False(await session.UploadIfDifferentSizeAsync(file.PathName, remote, ct: rig.Token));
        var deployment = new ClientSoftwareDeployment(client, session, staging, _ => { });
        var request = new SoftwareDeploymentRequest(SoftwareAction.Exe, Levels.System, file.PathName, "", "/quiet", "", "default");
        await deployment.DeployAsync(request);
        await deployment.DeployAsync(request);
        Assert.Equal(new[] { Cmd.FsList, Cmd.FsList, Cmd.InstallRun, Cmd.FsList, Cmd.InstallRun }, rig.Calls.Select(call => call.Command));
        Assert.All(rig.Calls.Where(call => call.Command == Cmd.InstallRun), call => Assert.Equal(remote, call.Args["path"]!.GetValue<string>()));
        Assert.All(rig.Calls.Where(call => call.Command == Cmd.FsList), call => Assert.Equal(Path.GetDirectoryName(remote), call.Args["path"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData(Levels.User)]
    [InlineData(Levels.System)]
    public async Task Reachable_remote_installer_never_uploads_or_deletes_caller_owned_file(string level)
    {
        await using var rig = new Rig();
        var client = rig.Client;
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        await new ClientSoftwareDeployment(client, session, "unused", _ => { })
            .DeployAsync(new(SoftwareAction.Msi, level, "missing-local-file", @"D:\packages\fixture.msi", "/qn", "", "default"));
        var run = Assert.Single(rig.Calls);
        Assert.Equal(Cmd.InstallRun, run.Command);
        Assert.Equal(level, run.Level);
        Assert.Equal(@"D:\packages\fixture.msi", run.Args["path"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData(Cmd.FsWrite)]
    [InlineData(Cmd.RegistryImport)]
    public async Task Registry_always_cleans_up_and_cleanup_refusal_never_masks_import_or_upload_error(string failed)
    {
        using var file = new LocalFile();
        await using var rig = new Rig(false, failed, Cmd.FsDelete);
        var client = rig.Client;
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        var task = new ClientSoftwareDeployment(client, session, @"C:\fixture-staging", _ => { })
            .DeployAsync(new(SoftwareAction.Registry, Levels.User, file.PathName, "", "", "", "64"));
        if (failed.Length > 0) Assert.Contains(failed, (await Assert.ThrowsAsync<RemoteException>(() => task)).Message);
        else await task;
        Assert.Equal(failed == Cmd.FsWrite ? new[] { Cmd.FsWrite, Cmd.FsDelete }
            : new[] { Cmd.FsWrite, Cmd.RegistryImport, Cmd.FsDelete }, rig.Calls.Select(call => call.Command));
        if (failed != Cmd.FsWrite)
        {
            var import = Assert.Single(rig.Calls, call => call.Command == Cmd.RegistryImport);
            Assert.Equal(Levels.User, import.Level);
            Assert.Equal("64", import.Args["view"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task Staged_install_honors_timeout_and_reports_requested_delete_refusal(bool delete, bool failInstall, bool failDelete)
    {
        await using var rig = new Rig(false, failInstall ? Cmd.InstallRun : "", failDelete ? Cmd.StagingDelete : "");
        var client = rig.Client;
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        var task = new ClientSoftwareDeployment(client, session, "unused", _ => { })
            .InstallStagedAsync(new(Levels.System, @"C:\fixture-staging\tool.msi", "/qn", 77, delete, "fixture-namespace", "tool.msi"));
        if (failInstall || failDelete) await Assert.ThrowsAsync<RemoteException>(() => task); else await task;
        Assert.Equal(delete && !failInstall ? new[] { Cmd.InstallRun, Cmd.StagingDelete } : new[] { Cmd.InstallRun }, rig.Calls.Select(call => call.Command));
        var calls = rig.Calls.ToArray();
        Assert.Equal(77, calls[0].Args["timeoutSec"]!.GetValue<int>());
        if (calls.Length == 2)
        {
            Assert.Equal(Levels.System, calls[1].Level);
            Assert.Equal("fixture-namespace", calls[1].Args["namespace"]!.GetValue<string>());
            Assert.Equal("tool.msi", calls[1].Args["relativePath"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData(false, Levels.User)]
    [InlineData(true, Levels.System)]
    public async Task Winget_preserves_package_action_scope_and_level(bool uninstall, string level)
    {
        await using var rig = new Rig();
        var client = rig.Client;
        using var session = await ClientSession.OpenAsync(client, new HostSettings { UseTls = false, Psk = Key }, rig.Token);
        await new ClientSoftwareDeployment(client, session, "unused", _ => { })
            .DeployAsync(new(uninstall ? SoftwareAction.WingetUninstall : SoftwareAction.WingetInstall, level, "", "", "", "Fixture.Package", "default"));
        var call = Assert.Single(rig.Calls);
        Assert.Equal(Cmd.InstallWinget, call.Command);
        Assert.Equal(level, call.Level);
        Assert.Equal("Fixture.Package", call.Args["id"]!.GetValue<string>());
        Assert.Equal(uninstall ? "uninstall" : "install", call.Args["action"]!.GetValue<string>());
        Assert.Equal(level == Levels.User ? "user" : "machine", call.Args["scope"]!.GetValue<string>());
    }
}
