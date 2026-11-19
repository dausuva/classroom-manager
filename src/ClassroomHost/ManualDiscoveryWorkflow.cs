// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>Core-owned side effects for one manual scan. No public plugin authority is exposed.</summary>
public interface IManualDiscoveryRuntime<TBatch>
{
    bool CanScan { get; }
    bool IsCurrent { get; }
    void SetScanning(bool scanning);
    void Prepare();
    Task<TBatch> DiscoverAsync(CancellationToken cancellation);
    void Apply(TBatch batch);
    Task EnrolAsync(TBatch batch);
    void Save();
    Task RefreshAsync();
    void Completed();
}

/// <summary>Shared admission, observation cancellation and accepted-state drain for manual discovery.</summary>
public sealed class ManualDiscoveryWorkflow(DiscoveryWorkCoordinator coordinator)
{
    public async Task<bool> RunAsync<TBatch>(IManualDiscoveryRuntime<TBatch> runtime, CancellationToken cancellation = default)
    {
        if (!runtime.CanScan) return false;
        cancellation.ThrowIfCancellationRequested();
        using var lease = coordinator.TryBeginScan();
        if (lease is null) return false;
        runtime.SetScanning(true);
        try
        {
            runtime.Prepare();
            var batch = await runtime.DiscoverAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!runtime.IsCurrent) return true;
            runtime.Apply(batch);
            // Accepted roster/trust work drains; even a partial enrolment failure must reach persistence.
            try { await runtime.EnrolAsync(batch); }
            catch (Exception enrolmentError)
            {
                try { runtime.Save(); }
                catch (Exception saveError) { throw new AggregateException(enrolmentError, saveError); }
                throw;
            }
            runtime.Save();
            cancellation.ThrowIfCancellationRequested();
            if (!runtime.IsCurrent) return true;
            await runtime.RefreshAsync();
            runtime.Completed();
            return true;
        }
        finally { runtime.SetScanning(false); }
    }
}
