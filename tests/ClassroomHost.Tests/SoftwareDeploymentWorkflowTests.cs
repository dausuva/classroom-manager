// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

// T-222 MW-B: preserve three distinct cleanup policies; no machine changes.
public sealed class SoftwareDeploymentWorkflowTests
{
    sealed class Runtime : IInstallerDeploymentRuntime, IRegistryDeploymentRuntime
    {
        public List<string> Calls = [];
        public HashSet<string> Fail = [];
        public Func<string, Task>? Work;
        Task Call(string name)
        {
            Calls.Add(name);
            if (Fail.Contains(name)) throw new IOException(name);
            return Work?.Invoke(name) ?? Task.CompletedTask;
        }
        public Task UploadAsync() => Call("upload");
        public Task InstallAsync() => Call("install");
        public Task ImportAsync() => Call("import");
        public Task DeleteAsync() => Call("delete");
    }

    [Theory]
    [InlineData(true, true, true, "upload,install,delete")]
    [InlineData(false, false, true, "install")]
    [InlineData(false, true, false, "install,delete")]
    [InlineData(false, false, false, "install")]
    public async Task Install_order_keeps_upload_and_success_cleanup_policy(bool upload, bool delete, bool bestEffort, string calls)
    {
        var runtime = new Runtime();
        await SoftwareDeploymentWorkflow.InstallAsync(new(upload, delete, bestEffort), runtime);
        Assert.Equal(calls.Split(','), runtime.Calls);
    }

    [Theory]
    [InlineData("upload", "upload")]
    [InlineData("install", "upload,install")]
    public async Task Failed_uploaded_install_is_retained_for_diagnosis_or_retry(string failed, string calls)
    {
        var runtime = new Runtime { Fail = [failed] };
        await Assert.ThrowsAsync<IOException>(() => SoftwareDeploymentWorkflow.InstallAsync(new(true, true, true), runtime));
        Assert.Equal(calls.Split(','), runtime.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Normal_cleanup_is_best_effort_but_requested_staged_cleanup_is_reported(bool bestEffort)
    {
        var runtime = new Runtime { Fail = ["delete"] };
        var task = SoftwareDeploymentWorkflow.InstallAsync(new(false, true, bestEffort), runtime);
        if (bestEffort) await task;
        else await Assert.ThrowsAsync<IOException>(() => task);
        Assert.Equal(new[] { "install", "delete" }, runtime.Calls);
    }

    [Theory]
    [InlineData("", "upload,import,delete")]
    [InlineData("upload", "upload,delete")]
    [InlineData("import", "upload,import,delete")]
    public async Task Registry_always_attempts_cleanup_without_masking_primary_failure(string failed, string calls)
    {
        var runtime = new Runtime { Fail = ["delete"] };
        if (failed.Length > 0) runtime.Fail.Add(failed);
        var task = SoftwareDeploymentWorkflow.ImportRegistryAsync(runtime);
        if (failed.Length == 0) await task;
        else Assert.Equal(failed, (await Assert.ThrowsAsync<IOException>(() => task)).Message);
        Assert.Equal(calls.Split(','), runtime.Calls);
    }

    [Fact]
    public async Task Upload_is_awaited_before_install_and_cleanup()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime { Work = name => name == "upload" ? release.Task : Task.CompletedTask };
        var task = SoftwareDeploymentWorkflow.InstallAsync(new(true, true, true), runtime);
        Assert.Equal(new[] { "upload" }, runtime.Calls);
        release.SetResult();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "upload", "install", "delete" }, runtime.Calls);
    }

    sealed class Heartbeat : IInstallerHeartbeatRuntime
    {
        public List<string> Calls = [];
        public TaskCompletionSource<int?> Reply = new();
        public int Waits;
        public bool Fail;
        public TimeSpan Elapsed { get; private set; }
        public void Starting() => Calls.Add("start");
        public Task<int?> RunAsync() { Calls.Add("run"); return Reply.Task; }
        public Task WaitAsync(Task request)
        {
            Elapsed = TimeSpan.FromSeconds(++Waits == 1 ? 1 : Waits == 2 ? 15 : 30);
            if (Waits == 4)
            {
                if (Fail) Reply.SetException(new IOException("installer"));
                else Reply.SetResult(3010);
            }
            return Task.CompletedTask;
        }
        public void Running(bool logHeartbeat) => Calls.Add(logHeartbeat ? "heartbeat" : "status");
        public void Completed(int? exitCode) => Calls.Add("exit:" + exitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Heartbeats_update_status_each_tick_log_every_fifteen_seconds_and_do_not_fabricate_success(bool fail)
    {
        var runtime = new Heartbeat { Fail = fail };
        var task = InstallerHeartbeatWorkflow.RunAsync(runtime);
        if (fail) await Assert.ThrowsAsync<IOException>(() => task);
        else await task;
        Assert.Equal(fail ? new[] { "start", "run", "status", "heartbeat", "heartbeat" }
            : new[] { "start", "run", "status", "heartbeat", "heartbeat", "exit:3010" }, runtime.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3010)]
    public async Task Immediate_installer_completion_does_not_wait_or_emit_spurious_heartbeat(int? exitCode)
    {
        var runtime = new Heartbeat();
        runtime.Reply.SetResult(exitCode);
        await InstallerHeartbeatWorkflow.RunAsync(runtime);
        Assert.Equal(new[] { "start", "run", "exit:" + exitCode }, runtime.Calls);
        Assert.Equal(0, runtime.Waits);
    }
}
