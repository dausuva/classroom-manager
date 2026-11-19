// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace ClassroomManager.Host;

public sealed record OperationComputerResult(string Computer, string State, string Message);

/// <summary>
/// Runs an action on every target through the main window's Operations center and activity log.
/// <paramref name="targetFinished"/> is raised once per target with an empty error on success.
/// </summary>
public delegate Task BulkOperationRunner(List<ClientInfo> targets, string title,
    Func<ClientInfo, ClientSession, Task> action, int maxParallel, string warnings,
    Action<ClientInfo, string> targetFinished);

public sealed class OperationActivity : INotifyPropertyChanged
{
    int _completed;
    int _succeeded;
    int _failed;
    int _cancelled;
    string _state = "Queued";
    string _latest = "Waiting to start";
    string _recovery = "No action is needed yet.";
    DateTime? _finished;
    CancellationTokenSource? _cancellation;
    Func<Task>? _retryFailed;
    Func<Task>? _undo;

    public OperationActivity(string title, string target, int total, DateTime? started = null, string warnings = "")
    {
        Title = title;
        Target = target;
        Total = total;
        Started = started ?? DateTime.Now;
        Warnings = warnings.Trim();
    }

    public string Title { get; }
    public string Target { get; }
    public int Total { get; }
    public DateTime Started { get; }
    public string StartedText => Started.ToString("HH:mm:ss");
    public string Warnings { get; }
    public bool HasWarnings => Warnings.Length > 0;
    public int Completed { get => _completed; private set => Set(ref _completed, value); }
    public int Succeeded { get => _succeeded; private set => Set(ref _succeeded, value); }
    public int Failed { get => _failed; private set => Set(ref _failed, value); }
    public int Cancelled { get => _cancelled; private set => Set(ref _cancelled, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public string Latest { get => _latest; private set => Set(ref _latest, value); }
    public string Recovery { get => _recovery; private set => Set(ref _recovery, value); }
    public string ProgressText => $"{Completed}/{Total}";
    public string ElapsedText => FormatElapsed((_finished ?? DateTime.Now) - Started);
    public bool IsFinished => State is "Completed" or "Completed with warnings" or "Failed" or "Cancelled";
    public bool CanCancel => !IsFinished && _cancellation is not null && !_cancellation.IsCancellationRequested;
    public bool CanRetry => IsFinished && Failed > 0 && _retryFailed is not null;
    /// <summary>True while what this operation did on the computers it succeeded on can still be taken back.</summary>
    public bool CanUndo => IsFinished && Succeeded > 0 && _undo is not null;
    /// <summary>What Undo does, in the words of the action that does it: "Unlock screen".</summary>
    public string UndoLabel { get; private set; } = "";
    public ObservableCollection<OperationComputerResult> Results { get; } = new();

    public void Start()
    {
        State = "Running";
        Latest = "Connecting to computers...";
    }

    public void ReportTransfer(string computer, FileTransferProgress progress)
    {
        if (State != "Running") return; // Late callbacks must not replace terminal/cancellation summaries.
        Latest = FileTransferProgressText.Format(progress, computer);
    }

    public void EnableCancellation(CancellationTokenSource cancellation)
    {
        _cancellation = cancellation;
        OnPropertyChanged(nameof(CanCancel));
    }

    public void Cancel()
    {
        if (!CanCancel) return;
        _cancellation!.Cancel();
        State = "Cancelling";
        Latest = "Finishing active computers; queued computers will be skipped.";
        OnPropertyChanged(nameof(CanCancel));
    }

    public void RecordSuccess(string computer)
    {
        Succeeded++;
        Completed++;
        Latest = $"Completed on {computer}";
        Results.Add(new OperationComputerResult(computer, "Succeeded", "Completed"));
        NotifyProgress();
    }

    public void RecordFailure(string computer, string message)
    {
        Failed++;
        Completed++;
        Latest = $"Failed on {computer}: {message}";
        Results.Add(new OperationComputerResult(computer, "Failed", message));
        NotifyProgress();
    }

    public void RecordCancelled(string computer)
    {
        Cancelled++;
        Completed++;
        Results.Add(new OperationComputerResult(computer, "Skipped", "Cancelled before this computer started"));
        NotifyProgress();
    }

    public void Finish()
    {
        var outcome = OperationResultLanguage.Format(Succeeded, Failed);
        State = outcome.State;
        Latest = outcome.Summary;
        Recovery = outcome.Recovery;
        FinishClock();
    }

    public void FinishCancelled()
    {
        State = "Cancelled";
        Latest = $"{Succeeded} succeeded; {Failed} failed; {Cancelled} skipped.";
        Recovery = "Review completed computers below. Start a new operation only for targets that still need the action.";
        FinishClock();
    }

    public void EnableRetry(Func<Task> retryFailed)
    {
        _retryFailed = retryFailed;
        OnPropertyChanged(nameof(CanRetry));
    }

    /// <summary>Says how this operation is taken back on the computers it succeeded on.</summary>
    public void EnableUndo(string label, Func<Task> undo)
    {
        UndoLabel = label;
        _undo = undo;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoLabel));
    }

    /// <summary>Takes the operation back, once.</summary>
    public async Task UndoAsync()
    {
        if (!CanUndo) return;
        var undo = _undo!;
        _undo = null;
        OnPropertyChanged(nameof(CanUndo));
        await undo();
    }

    public async Task RetryFailedAsync()
    {
        if (!CanRetry) return;
        var retry = _retryFailed!;
        _retryFailed = null;
        OnPropertyChanged(nameof(CanRetry));
        await retry();
    }

    public void RefreshElapsed() => OnPropertyChanged(nameof(ElapsedText));

    void FinishClock()
    {
        _finished = DateTime.Now;
        RefreshElapsed();
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanUndo));
    }

    static string FormatElapsed(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes}:{value.Seconds:00}";

    public string FormatDetails()
    {
        var report = new StringBuilder()
            .AppendLine($"Operation: {Title}")
            .AppendLine($"State: {State}")
            .AppendLine($"Target: {Target}")
            .AppendLine($"Started: {Started:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"Result: {Succeeded} succeeded; {Failed} failed; {Cancelled} skipped")
            .AppendLine($"Elapsed: {ElapsedText}")
            .AppendLine($"Next step: {Recovery}")
            .AppendLine();
        if (HasWarnings) report.AppendLine("Warnings:").AppendLine(Warnings).AppendLine();
        foreach (var result in Results)
            report.AppendLine($"{result.State}  {result.Computer}  {result.Message}");
        return report.ToString().TrimEnd();
    }

    void NotifyProgress() => OnPropertyChanged(nameof(ProgressText));

    void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The Operations view toggle keeps one caption; running work shows as a number in a badge of fixed width
/// beside it, so the buttons after it never move while work starts and ends.
/// </summary>
public static class OperationViewLabel
{
    public const string Caption = "_Operations";

    public static string Badge(int running) => running <= 0 ? "" : running > 99 ? "99+" : running.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>What a screen reader says about the toggle besides its name.</summary>
    public static string Status(int running) => running <= 0 ? "" : Loc.F($"{running} running");
}

public sealed record OperationResultText(string State, string Summary, string Recovery);

public static class OperationResultLanguage
{
    public static OperationResultText Format(int succeeded, int failed)
    {
        if (failed == 0)
            return new OperationResultText("Completed",
                $"Completed successfully on {succeeded} computer(s).",
                "No recovery action is needed.");
        if (succeeded == 0)
            return new OperationResultText("Failed",
                $"Failed on all {failed} computer(s).",
                "Expand this operation, review each error, correct the cause, then retry only when the action is safe to repeat.");
        return new OperationResultText("Completed with warnings",
            $"{succeeded} succeeded; {failed} failed.",
            "Successful computers need no further action. Expand this operation and review only the failed computers.");
    }
}
