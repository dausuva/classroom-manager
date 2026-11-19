// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows.Threading;

namespace ClassroomManager.Host;

/// <summary>UI-thread timer adapter. Create and dispose on the supplied dispatcher's thread.</summary>
public sealed class DispatcherRecurringTimer : IDisposable
{
    readonly DispatcherTimer _timer;
    EventHandler? _handler;

    public DispatcherRecurringTimer(Dispatcher dispatcher, TimeSpan interval, Action tick)
    {
        dispatcher.VerifyAccess();
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = interval };
        _handler = (_, _) => tick();
        _timer.Tick += _handler;
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Dispatcher.VerifyAccess();
        _timer.Stop();
        if (_handler is null) return;
        _timer.Tick -= _handler;
        _handler = null;
    }
}
