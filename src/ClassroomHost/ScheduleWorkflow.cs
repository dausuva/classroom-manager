// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Automation;

namespace ClassroomManager.Host;

/// <summary>T-217/T-222: core adapters for the shared GUI/background scheduling module.</summary>
public sealed class ScheduleWorkflow(string dailyPath, string computersPath, TimeProvider? clock = null)
{
    public async Task<int> RunTimedAsync(IEnumerable<ScheduledAction> schedules, DateTime now,
        Func<ScheduledAction, CancellationToken, Task> execute, Action<ScheduledAction, Exception> failed, CancellationToken ct = default)
    {
        var runtime = new Runtime(schedules, now, dailyPath, computersPath, timed: execute, failed: failed);
        return await new SchedulingModule(runtime, clock).RunTimedAsync(runtime.Plans, ct);
    }

    public async Task<List<CliResult>> RunCatchUpAsync(IEnumerable<ScheduledAction> schedules, DateTime now,
        Func<ScheduledAction, List<SavedComputer>> targets,
        Func<ScheduledAction, List<SavedComputer>, Action<string>, CancellationToken, Task<List<CliResult>>> execute,
        Action<string> log, CancellationToken ct = default)
    {
        var runtime = new Runtime(schedules, now, dailyPath, computersPath, targets: targets, catchUp: execute, log: log);
        var results = await new SchedulingModule(runtime, clock).RunCatchUpAsync(runtime.Plans, ct);
        return results.Select(result => new CliResult($"{result.ScheduleName}: {result.Result.Computer}",
            result.Result.Address, result.Result.Room, result.Result.Ok, result.Result.Detail)).ToList();
    }

    sealed class Runtime : IScheduleRuntime
    {
        readonly Dictionary<string, ScheduledAction> _schedules;
        readonly DateTime _now;
        readonly string _dailyPath, _computersPath;
        readonly Func<ScheduledAction, CancellationToken, Task>? _timed;
        readonly Action<ScheduledAction, Exception>? _failed;
        readonly Func<ScheduledAction, List<SavedComputer>>? _targets;
        readonly Func<ScheduledAction, List<SavedComputer>, Action<string>, CancellationToken, Task<List<CliResult>>>? _catchUp;
        readonly Action<string>? _log;
        public IReadOnlyList<SchedulePlan> Plans { get; }

        public Runtime(IEnumerable<ScheduledAction> schedules, DateTime now, string dailyPath, string computersPath,
            Func<ScheduledAction, CancellationToken, Task>? timed = null, Action<ScheduledAction, Exception>? failed = null,
            Func<ScheduledAction, List<SavedComputer>>? targets = null,
            Func<ScheduledAction, List<SavedComputer>, Action<string>, CancellationToken, Task<List<CliResult>>>? catchUp = null,
            Action<string>? log = null)
        {
            var selected = schedules.ToArray();
            _schedules = selected.ToDictionary(schedule => schedule.Id, StringComparer.Ordinal);
            _now = now;
            _dailyPath = dailyPath;
            _computersPath = computersPath;
            _timed = timed;
            _failed = failed;
            _targets = targets;
            _catchUp = catchUp;
            _log = log;
            Plans = Array.AsReadOnly(selected.Select(schedule =>
            {
                var catchesUp = ScheduleRuns.CatchesUp(schedule);
                var at = catchesUp ? ScheduleRuns.LatestOccurrence(schedule, now) : null;
                return new SchedulePlan(schedule.Id, schedule.Name, catchesUp, !catchesUp && ScheduleRuns.IsDue(schedule, now),
                    at is { } occurrence ? ScheduleRuns.OccurrenceKey(occurrence) : null);
            }).ToArray());
        }

        public IScheduleRun? TryAcquire(SchedulePlan schedule)
        {
            var ownership = ScheduleOwnership.TryAcquire(schedule.Id, _computersPath);
            return ownership is null ? null : new Run(this, _schedules[schedule.Id], schedule.Occurrence, ownership);
        }
        public string DescribeFailure(Exception error) => ErrorClassifier.Describe(error);
        public ScheduleResult MissingResult(ScheduleTarget target) => new(target.Computer, target.Address, target.Room, false, "No result was recorded.");
        public void TimedFailure(SchedulePlan schedule, Exception error) => _failed!(_schedules[schedule.Id], error);

        sealed class Run(Runtime runtime, ScheduledAction schedule, string? occurrence, ScheduleOwnership ownership) : IScheduleRun
        {
            Dictionary<string, SavedComputer> _byKey = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SavedComputer> _byAddress = new(StringComparer.OrdinalIgnoreCase);

            public Task<bool> ClaimTimedAsync(CancellationToken cancellation) =>
                Task.Run(() => ScheduleRuns.TryClaim(schedule.Id, runtime._now, runtime._dailyPath), cancellation);

            public IReadOnlyList<ScheduleTarget> ClaimTargets(DateTime utcNow)
            {
                var selected = runtime._targets!(schedule).GroupBy(ScheduleRuns.ComputerKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                var claimed = ScheduleRuns.ClaimDue(schedule.Id, occurrence!, selected.Keys, utcNow, runtime._computersPath);
                _byKey = claimed.ToDictionary(key => key, key => selected[key], StringComparer.OrdinalIgnoreCase);
                _byAddress = _byKey.Values.ToDictionary(computer => computer.Address, StringComparer.OrdinalIgnoreCase);
                return Array.AsReadOnly(_byKey.Select(pair => new ScheduleTarget(pair.Key, pair.Value.Name, pair.Value.Address, pair.Value.Room)).ToArray());
            }

            public void BeforeDispatch(string targetKey)
            {
                if (!_byKey.ContainsKey(targetKey)) throw new InvalidOperationException();
                if (schedule.Action == "command")
                    ScheduleRuns.MarkDispatch(schedule.Id, occurrence!, targetKey, ownership.Owner, runtime._computersPath);
            }

            public Task ExecuteTimedAsync(CancellationToken cancellation) => runtime._timed!(schedule, cancellation);

            public async Task<IReadOnlyList<ScheduleResult>> ExecuteCatchUpAsync(IReadOnlyList<ScheduleTarget> targets,
                Action<string> beforeDispatch, CancellationToken cancellation)
            {
                var computers = targets.Select(target => _byKey[target.Key]).ToList();
                var results = await runtime._catchUp!(schedule, computers, beforeDispatch, cancellation);
                return Array.AsReadOnly(results.Select(result => new ScheduleResult(result.Computer, result.Address, result.Room, result.Ok, result.Detail)).ToArray());
            }

            string Key(ScheduleResult result) => ScheduleRuns.ComputerKey(_byAddress[result.Address]);
            public void MarkSuccessful(IReadOnlyList<ScheduleResult> results) =>
                ScheduleRuns.MarkDone(schedule.Id, occurrence!, results.Where(result => result.Ok).Select(Key), runtime._computersPath);
            public bool IsUnknown(ScheduleResult result) =>
                ScheduleRuns.UnknownCount(schedule.Id, occurrence!, [Key(result)], runtime._computersPath) > 0;
            public bool ShouldReportFailure(ScheduleResult result) => !ErrorClassifier.IsUnreachable(result.Detail) &&
                ScheduleRuns.NoteFailure(schedule.Id, occurrence!, Key(result), result.Detail, runtime._computersPath);
            public void Report(ScheduleResult result, bool uncertain)
            {
                var detail = uncertain ? "Outcome unknown; not retried automatically. " + result.Detail : result.Detail;
                runtime._log!($"Schedule \"{schedule.Name}\" (run of {occurrence}): {(result.Ok ? "OK  " : "FAIL")}  {result.Computer}: {detail}");
            }
            public void Dispose() => ownership.Dispose();
        }
    }
}
