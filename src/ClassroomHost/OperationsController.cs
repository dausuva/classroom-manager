// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// The Operations list of the Host window (MW-E): one <see cref="OperationActivity"/> per run, what a run does to the computers it covers
/// (busy count, status, log lines), and the cancel, retry and undo it offers afterwards. The connecting, throttling and per-computer
/// isolation stay in <see cref="BulkRunner"/>, which ClassroomCli shares; this adds only what the window shows about a run. It holds no
/// window, so the window keeps its handlers thin and the rules are tested without one.
/// </summary>
public sealed class OperationsController
{
    /// <summary>Operations kept in the list; the oldest finished ones go first.</summary>
    public const int Kept = 100;

    /// <summary>The number of computers a run works on at once unless the caller says otherwise.</summary>
    public const int DefaultParallel = 8;

    /// <summary>How a run is executed: <see cref="BulkRunner.RunAsync"/>, or a stand-in in a test.</summary>
    public delegate Task Executor(IReadOnlyList<ClientInfo> targets, Func<ClientInfo, ClientSession, Task> action, int maxParallel,
        CancellationToken cancellation, BulkRunCallbacks callbacks, bool allowExclusiveOperation);

    readonly Executor _execute;
    readonly Action<string, string?> _log;

    public OperationsController(HostSettings settings, Action<string, string?> log, Executor? execute = null)
    {
        _log = log;
        _execute = execute ?? ((targets, action, maxParallel, cancellation, callbacks, exclusive) =>
            BulkRunner.RunAsync(targets, settings, action, maxParallel, cancellation, callbacks, exclusive));
    }

    public ObservableCollection<OperationActivity> Operations { get; } = new();

    /// <summary>Raised when a run is added to the list, so the window can select it.</summary>
    public event Action<OperationActivity>? Added;

    public int Running => Operations.Count(item => !item.IsFinished);

    public void RefreshElapsed()
    {
        foreach (var operation in Operations.Where(item => !item.IsFinished)) operation.RefreshElapsed();
    }

    /// <summary>Takes the finished operations out of the list; returns how many went.</summary>
    public int ClearFinished()
    {
        var finished = Operations.Where(item => item.IsFinished).ToList();
        foreach (var operation in finished) Operations.Remove(operation);
        return finished.Count;
    }

    /// <summary>
    /// Runs <paramref name="action"/> on every target and records it as an operation (unless <paramref name="quiet"/>).
    /// <paramref name="maxParallel"/>: -1 the default limit, 0 everyone at once, n computers at a time. <paramref name="undo"/> is the action that
    /// takes this one back, offered for the computers this one succeeded on. <paramref name="allowRetry"/> offers a retry of the computers that failed.
    /// </summary>
    public async Task RunAsync(List<ClientInfo> targets, string title, Func<ClientInfo, ClientSession, Task> action,
        bool quiet = false, int maxParallel = -1, bool allowExclusiveOperation = false, bool allowRetry = false,
        string warnings = "", Action<ClientInfo, string>? targetFinished = null,
        (string Label, Func<ClientInfo, ClientSession, Task> Action)? undo = null, string? englishTitle = null)
    {
        var succeededTargets = new ConcurrentBag<ClientInfo>();
        int limit = maxParallel < 0 ? DefaultParallel : maxParallel == 0 ? Math.Max(1, targets.Count) : maxParallel;
        OperationActivity? operation = null;
        using var cancellation = new CancellationTokenSource();
        var failedTargets = new ConcurrentBag<ClientInfo>();
        if (!quiet)
        {
            var targetText = OperationTargetSummary.Format(targets);
            operation = new OperationActivity(title,
                targetText.Heading.Replace("Target: ", "", StringComparison.Ordinal) + " · " + targetText.Detail,
                targets.Count, warnings: warnings);
            Operations.Insert(0, operation);
            Added?.Invoke(operation);
            while (Operations.Count > Kept)
            {
                var oldestFinished = Operations.LastOrDefault(item => item.IsFinished);
                if (oldestFinished is null) break;
                Operations.Remove(oldestFinished);
            }
            operation.Start();
            operation.EnableCancellation(cancellation);
            var concurrencyLabel = englishTitle is null ? "" : limit >= targets.Count
                ? Loc.T("all at once") : Loc.Current.Format($"{limit} at a time");
            _log($"{englishTitle ?? title}: {targets.Count} computer(s), {(limit >= targets.Count ? "all at once" : limit + " at a time")}...",
                englishTitle is null ? null : Loc.Current.Format($"{title}: {targets.Count} computer(s), {concurrencyLabel}..."));
        }
        var errors = new ConcurrentDictionary<ClientInfo, string>();
        await _execute(targets, async (client, session) =>
        {
            void Progress(FileTransferProgress progress) => operation?.ReportTransfer(client.Name, progress);
            session.TransferProgress += Progress;
            try { await action(client, session); }
            finally { session.TransferProgress -= Progress; }
        }, limit, cancellation.Token, new BulkRunCallbacks
        {
            Cancelled = c =>
            {
                operation?.RecordCancelled(c.Name);
                targetFinished?.Invoke(c, "Cancelled before this computer started");
            },
            Starting = c => { if (!quiet) Interlocked.Increment(ref c.BusyCount); },
            Succeeded = c =>
            {
                succeededTargets.Add(c);
                operation?.RecordSuccess(c.Name);
                if (!quiet) _log($"  OK    {c.Name}", null);
                if (!quiet && !c.Status.StartsWith("Done") && !c.Status.StartsWith("Sent") &&
                    !c.Status.StartsWith("Collected") && !c.Status.StartsWith("Update"))
                    c.Status = c.CompatibleOnlineStatus;
            },
            Failed = (c, ex) =>
            {
                // The message with its stable error id (T-134), so it can be looked up in docs/ERROR-CODES.md.
                var shown = ErrorClassifier.Describe(ex);
                errors[c] = shown;
                c.Status = quiet ? "Offline" : "Error: " + shown;
                if (quiet && ex is AuthException) c.Status = KeyRefusalPause.IsPauseMessage(ex.Message) || KeyRefusalPause.EndsAt(c) is not null ? "Waiting: ignores this PC" : "Wrong key";
                operation?.RecordFailure(c.Name, shown);
                failedTargets.Add(c);
                if (!quiet) _log($"  FAIL  {c.Name}: {shown}", null);
            },
            Finished = c =>
            {
                if (!quiet) Interlocked.Decrement(ref c.BusyCount);
                targetFinished?.Invoke(c, errors.TryGetValue(c, out var error) ? error : "");
            },
        }, allowExclusiveOperation);

        if (quiet) return;
        if (cancellation.IsCancellationRequested) operation!.FinishCancelled();
        else operation!.Finish();
        if (!cancellation.IsCancellationRequested && allowRetry && failedTargets.Count > 0)
            operation.EnableRetry(() => RunAsync(failedTargets.ToList(), title + " — retry failed", action,
                maxParallel: maxParallel, allowExclusiveOperation: allowExclusiveOperation, allowRetry: true,
                warnings: warnings, englishTitle: englishTitle is null ? null : englishTitle + " — retry failed"));
        if (undo is { } back && !succeededTargets.IsEmpty)
            operation.EnableUndo(back.Label, () => RunAsync(succeededTargets.ToList(), back.Label, back.Action, allowRetry: true));
        _log($"{englishTitle ?? title}: done.", englishTitle is null ? null : Loc.Current.Format($"{title}: done."));
    }
}
