// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed record SoftwareDeploymentRequest(SoftwareAction Action, string Level, string LocalFile,
    string RemotePath, string Arguments, string PackageId, string RegistryView);
public sealed record StagedInstallerRequest(string Level, string RemotePath, string Arguments, int TimeoutSeconds,
    bool DeleteAfterSuccess, string Namespace, string RelativePath);

/// <summary>Non-owning authenticated command adapter for the trusted built-in deployment feature.</summary>
public sealed class ClientSoftwareDeployment(ClientInfo client, ClientSession session, string staging, Action<string> log)
{
    public async Task DeployAsync(SoftwareDeploymentRequest request)
    {
        if (request.Action is SoftwareAction.WingetInstall or SoftwareAction.WingetUninstall)
        {
            var uninstall = request.Action == SoftwareAction.WingetUninstall;
            client.Status = uninstall ? "Checking winget package..." : "Resolving winget package...";
            await Task.Delay(50);
            client.Status = uninstall ? "Removing package (winget)..." : "Downloading/installing package (winget)...";
            await session.CallAtAsync(request.Level, Cmd.InstallWinget, new JsonObject
            {
                ["id"] = request.PackageId, ["action"] = uninstall ? "uninstall" : "install",
                ["scope"] = request.Level == Levels.User ? "user" : "machine",
            });
        }
        else if (request.Action == SoftwareAction.Registry)
            await SoftwareDeploymentWorkflow.ImportRegistryAsync(new RegistryRuntime(this, request));
        else
        {
            var uploaded = request.RemotePath.Length == 0;
            // Share the Temporary uploads location so a previously staged installer is reused.
            var remote = uploaded ? Path.Combine(staging, "install", Path.GetFileName(request.LocalFile)) : request.RemotePath;
            await SoftwareDeploymentWorkflow.InstallAsync(new(uploaded, false, true),
                new InstallerRuntime(this, request, remote));
        }
        client.Status = "Done";
    }

    public async Task InstallStagedAsync(StagedInstallerRequest request) =>
        await SoftwareDeploymentWorkflow.InstallAsync(new(false, request.DeleteAfterSuccess, false), new StagedRuntime(this, request));

    IProgress<double> Progress() => new Progress<double>(p => client.Status = $"Uploading {p:P0}");
    Task DeleteAsync(string remote) => session.CallAsync(Cmd.FsDelete, new JsonObject { ["path"] = remote });
    Task RunInstallerAsync(string level, string remote, string arguments, int timeout) =>
        InstallerHeartbeatWorkflow.RunAsync(new HeartbeatRuntime(client, session, log, level, remote, arguments, timeout));

    sealed class RegistryRuntime(ClientSoftwareDeployment owner, SoftwareDeploymentRequest request) : IRegistryDeploymentRuntime
    {
        string Remote => $@"{owner.Staging}\{Path.GetFileName(request.LocalFile)}";
        public Task UploadAsync() => owner.Session.UploadAsync(request.LocalFile, Remote, owner.Progress());
        public async Task ImportAsync()
        {
            owner.Client.Status = $"Applying registry patch ({request.RegistryView}-bit view)...";
            await owner.Session.CallAtAsync(request.Level, Cmd.RegistryImport, new JsonObject
            { ["path"] = Remote, ["view"] = request.RegistryView });
        }
        public Task DeleteAsync() => owner.DeleteAsync(Remote);
    }

    sealed class InstallerRuntime(ClientSoftwareDeployment owner, SoftwareDeploymentRequest request, string remote) : IInstallerDeploymentRuntime
    {
        public async Task UploadAsync()
        {
            if (!await owner.Session.UploadIfDifferentSizeAsync(request.LocalFile, remote, owner.Progress()))
                owner.Client.Status = "Reusing matching staged installer...";
        }
        public async Task InstallAsync()
        {
            owner.Client.Status = remote.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? "Starting Windows Installer..." : "Starting installer...";
            await owner.RunInstallerAsync(request.Level, remote, request.Arguments, 1800);
        }
        public Task DeleteAsync() => owner.DeleteAsync(remote);
    }

    sealed class StagedRuntime(ClientSoftwareDeployment owner, StagedInstallerRequest request) : IInstallerDeploymentRuntime
    {
        public Task UploadAsync() => throw new NotSupportedException();
        public Task InstallAsync() => owner.RunInstallerAsync(request.Level, request.RemotePath, request.Arguments, request.TimeoutSeconds);
        public Task DeleteAsync() => owner.Session.CallAsync(Cmd.StagingDelete, new JsonObject
        { ["namespace"] = request.Namespace, ["relativePath"] = request.RelativePath });
    }

    sealed class HeartbeatRuntime(ClientInfo client, ClientSession session, Action<string> log, string level,
        string remotePath, string arguments, int timeoutSec) : IInstallerHeartbeatRuntime
    {
        readonly Stopwatch _started = Stopwatch.StartNew();
        readonly string _fileName = Path.GetFileName(remotePath);
        public TimeSpan Elapsed => _started.Elapsed;
        public void Starting()
        {
            client.Status = $"Starting {_fileName}...";
            log($"  START {client.Name}: {_fileName}; timeout {timeoutSec}s");
        }
        public async Task<int?> RunAsync()
        {
            var result = await session.CallAtAsync(level, Cmd.InstallRun, new JsonObject
            { ["path"] = remotePath, ["args"] = arguments, ["timeoutSec"] = timeoutSec });
            return result["exitCode"]?.GetValue<int>();
        }
        public async Task WaitAsync(Task request) => await Task.WhenAny(request, Task.Delay(1000));
        public void Running(bool logHeartbeat)
        {
            client.Status = InstallerProgress.RunningStatus(_fileName, Elapsed);
            if (logHeartbeat)
                log($"  WAIT  {client.Name}: {_fileName} still running after {InstallerProgress.FormatElapsed(Elapsed)}; no percentage is exposed by this installer");
        }
        public void Completed(int? exitCode)
        {
            client.Status = $"Done: {_fileName}" + (exitCode is null ? "" : $" (exit {exitCode})");
            log($"  EXIT  {client.Name}: {_fileName} completed after {InstallerProgress.FormatElapsed(Elapsed)}" +
                (exitCode is null ? "" : $" with code {exitCode}"));
        }
    }

    ClientInfo Client => client;
    ClientSession Session => session;
    string Staging => staging;
}
