// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

public sealed record InstallerDeployment(bool Upload, bool DeleteAfterSuccess, bool BestEffortDelete);

public interface IInstallerDeploymentRuntime
{
    Task UploadAsync();
    Task InstallAsync();
    Task DeleteAsync();
}

public interface IRegistryDeploymentRuntime
{
    Task UploadAsync();
    Task ImportAsync();
    Task DeleteAsync();
}

/// <summary>Typed sequencing only. Transport, paths, authorization and Windows execution remain in core adapters.</summary>
public static class SoftwareDeploymentWorkflow
{
    public static async Task InstallAsync(InstallerDeployment plan, IInstallerDeploymentRuntime runtime)
    {
        if (plan.Upload) await runtime.UploadAsync();
        await runtime.InstallAsync();
        if (!plan.DeleteAfterSuccess) return;
        if (plan.BestEffortDelete) { try { await runtime.DeleteAsync(); } catch { } }
        else await runtime.DeleteAsync();
    }

    public static async Task ImportRegistryAsync(IRegistryDeploymentRuntime runtime)
    {
        try { await runtime.UploadAsync(); await runtime.ImportAsync(); }
        finally { try { await runtime.DeleteAsync(); } catch { } }
    }
}

public interface IInstallerHeartbeatRuntime
{
    TimeSpan Elapsed { get; }
    void Starting();
    Task<int?> RunAsync();
    Task WaitAsync(Task request);
    void Running(bool logHeartbeat);
    void Completed(int? exitCode);
}

public static class InstallerHeartbeatWorkflow
{
    public static async Task RunAsync(IInstallerHeartbeatRuntime runtime)
    {
        runtime.Starting();
        var request = runtime.RunAsync();
        var nextLog = TimeSpan.FromSeconds(15);
        while (!request.IsCompleted)
        {
            await runtime.WaitAsync(request);
            if (request.IsCompleted) break;
            var log = runtime.Elapsed >= nextLog;
            runtime.Running(log);
            if (log) nextLog += TimeSpan.FromSeconds(15);
        }
        runtime.Completed(await request);
    }
}
