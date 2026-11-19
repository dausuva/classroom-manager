// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// Presentation adapter for the three legacy process-wide window callbacks. The newest live owner
/// supplies them; closure restores the previous live owner without resurrecting a closed window.
/// </summary>
public sealed class HostWindowCallbacks : IDisposable
{
    static readonly object Gate = new();

    Func<StepUpRequest, Task<StepUpAnswer>>? _prompt;
    Func<IReadOnlyList<ClientInfo>>? _computers;
    Func<string, int>? _countOpening;
    readonly Func<StepUpRequest, Task<StepUpAnswer>>? _previousPrompt;
    readonly Func<IReadOnlyList<ClientInfo>> _previousComputers;
    readonly Func<string, int>? _previousCountOpening;
    readonly Func<StepUpRequest, Task<StepUpAnswer>> _promptHandler;
    readonly Func<IReadOnlyList<ClientInfo>> _computersHandler;
    readonly Func<string, int> _countHandler;
    bool _disposed;

    public bool IsDisposed => Volatile.Read(ref _disposed);

    public HostWindowCallbacks(Func<StepUpRequest, Task<StepUpAnswer>> prompt,
        Func<IReadOnlyList<ClientInfo>> computers, Func<string, int> countOpening)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(computers);
        ArgumentNullException.ThrowIfNull(countOpening);
        _prompt = prompt;
        _computers = computers;
        _countOpening = countOpening;
        _promptHandler = AskAsync;
        _computersHandler = Computers;
        _countHandler = CountOpening;
        lock (Gate)
        {
            _previousPrompt = StepUp.Prompt;
            _previousComputers = ComputerPicker.Computers;
            _previousCountOpening = UiPreferences.Instance.CountOpening;
            StepUp.Prompt = _promptHandler;
            ComputerPicker.Computers = _computersHandler;
            UiPreferences.Instance.CountOpening = _countHandler;
        }
    }

    async Task<StepUpAnswer> AskAsync(StepUpRequest request)
    {
        var prompt = Volatile.Read(ref _prompt);
        if (IsDisposed || prompt is null) return StepUpAnswer.Deny;
        var answer = await prompt(request).ConfigureAwait(false);
        return IsDisposed ? StepUpAnswer.Deny : answer;
    }

    IReadOnlyList<ClientInfo> Computers() => Volatile.Read(ref _computers)?.Invoke() ?? [];
    int CountOpening(string name) => Volatile.Read(ref _countOpening)?.Invoke(name) ?? 0;

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            Volatile.Write(ref _prompt, null);
            Volatile.Write(ref _computers, null);
            Volatile.Write(ref _countOpening, null);
            // A caller may have deliberately replaced any legacy property after registration.
            // Release only slots that still contain this owner's exact wrapper.
            if (ReferenceEquals(StepUp.Prompt, _promptHandler))
                StepUp.Prompt = LivePrevious(_previousPrompt, owner => owner._previousPrompt);
            if (ReferenceEquals(ComputerPicker.Computers, _computersHandler))
                ComputerPicker.Computers = LivePrevious(_previousComputers, owner => owner._previousComputers)!;
            if (ReferenceEquals(UiPreferences.Instance.CountOpening, _countHandler))
                UiPreferences.Instance.CountOpening = LivePrevious(_previousCountOpening, owner => owner._previousCountOpening);
        }
    }

    static TDelegate? LivePrevious<TDelegate>(TDelegate? candidate, Func<HostWindowCallbacks, TDelegate?> previous)
        where TDelegate : Delegate
    {
        while (candidate?.Target is HostWindowCallbacks owner && owner.IsDisposed)
            candidate = previous(owner);
        return candidate;
    }
}
