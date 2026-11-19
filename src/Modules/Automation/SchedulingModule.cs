// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Automation;

public sealed record SchedulePlan(string Id, string Name, bool CatchUp, bool TimedDue, string? Occurrence);
public sealed record ScheduleTarget(string Key, string Computer, string Address, string Room);
public sealed record ScheduleResult(string Computer, string Address, string Room, bool Ok, string Detail);
public sealed record ScheduledResult(string ScheduleName, ScheduleResult Result);

/// <summary>Core-owned lease/checkpoint and execution scope. Disposal releases ownership, never history.</summary>
public interface IScheduleRun : IDisposable
{
    Task<bool> ClaimTimedAsync(CancellationToken cancellation);
    IReadOnlyList<ScheduleTarget> ClaimTargets(DateTime utcNow);
    void BeforeDispatch(string targetKey);
    Task ExecuteTimedAsync(CancellationToken cancellation);
    Task<IReadOnlyList<ScheduleResult>> ExecuteCatchUpAsync(IReadOnlyList<ScheduleTarget> targets,
        Action<string> beforeDispatch, CancellationToken cancellation);
    void MarkSuccessful(IReadOnlyList<ScheduleResult> results);
    bool IsUnknown(ScheduleResult result);
    bool ShouldReportFailure(ScheduleResult result);
    void Report(ScheduleResult result, bool uncertain);
}

public interface IScheduleRuntime
{
    IScheduleRun? TryAcquire(SchedulePlan schedule);
    string DescribeFailure(Exception error);
    ScheduleResult MissingResult(ScheduleTarget target);
    void TimedFailure(SchedulePlan schedule, Exception error);
}

/// <summary>Typed orchestration only. No storage paths, settings, raw commands, credentials or transport.</summary>
public sealed class SchedulingModule(IScheduleRuntime runtime, TimeProvider? clock = null)
{
    public const string Id = "dausuva.automation.scheduling";
    public const int ContractVersion = 1;
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<int> RunTimedAsync(IReadOnlyList<SchedulePlan> schedules, CancellationToken cancellation = default)
    {
        var selected = Snapshot(schedules);
        var started = 0;
        foreach (var schedule in selected.Where(schedule => !schedule.CatchUp && schedule.TimedDue))
        {
            cancellation.ThrowIfCancellationRequested();
            using var run = runtime.TryAcquire(schedule);
            if (run is null || !await run.ClaimTimedAsync(cancellation)) continue;
            started++;
            try { await run.ExecuteTimedAsync(cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception error) { runtime.TimedFailure(schedule, error); }
        }
        return started;
    }

    public async Task<IReadOnlyList<ScheduledResult>> RunCatchUpAsync(IReadOnlyList<SchedulePlan> schedules,
        CancellationToken cancellation = default)
    {
        var selected = Snapshot(schedules);
        var results = new List<ScheduledResult>();
        foreach (var schedule in selected.Where(schedule => schedule.CatchUp))
        {
            cancellation.ThrowIfCancellationRequested();
            using var run = runtime.TryAcquire(schedule);
            if (run is null || schedule.Occurrence is null) continue;
            var targets = run.ClaimTargets(_clock.GetUtcNow().UtcDateTime).ToArray();
            if (targets.Length == 0) continue;
            var byAddress = targets.ToDictionary(target => target.Address, StringComparer.OrdinalIgnoreCase);
            var byKey = targets.ToDictionary(target => target.Key, StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<ScheduleResult> outcome;
            try
            {
                outcome = await run.ExecuteCatchUpAsync(Array.AsReadOnly(targets), key =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!byKey.ContainsKey(key)) throw new InvalidOperationException();
                    // Core decides which actions require durable uncertain-outcome checkpoints.
                    run.BeforeDispatch(key);
                }, cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                var detail = runtime.DescribeFailure(error);
                outcome = targets.Select(target => new ScheduleResult(target.Computer, target.Address, target.Room, false, detail)).ToArray();
            }
            cancellation.ThrowIfCancellationRequested();
            var scoped = outcome.Where(result => byAddress.ContainsKey(result.Address)).ToList();
            // Contradictory/duplicate terminal answers must never acknowledge an uncertain dispatch.
            if (scoped.Select(result => result.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count() != scoped.Count)
                throw new InvalidOperationException();
            foreach (var target in targets.Where(target => !scoped.Any(result => result.Address.Equals(target.Address, StringComparison.OrdinalIgnoreCase))))
                scoped.Add(runtime.MissingResult(target));
            var completed = Array.AsReadOnly(scoped.ToArray());
            run.MarkSuccessful(completed);
            foreach (var result in completed)
            {
                var uncertain = !result.Ok && run.IsUnknown(result);
                if (!result.Ok && !uncertain && !run.ShouldReportFailure(result)) continue;
                run.Report(result, uncertain);
            }
            results.AddRange(completed.Select(result => new ScheduledResult(schedule.Name, result)));
        }
        return Array.AsReadOnly(results.ToArray());
    }

    static SchedulePlan[] Snapshot(IReadOnlyList<SchedulePlan> schedules)
    {
        var snapshot = schedules.ToArray();
        if (snapshot.Any(schedule => string.IsNullOrWhiteSpace(schedule.Id)) ||
            snapshot.Select(schedule => schedule.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException(nameof(schedules));
        return snapshot;
    }
}
