// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.CompilerServices;
using System.Windows;

namespace ClassroomManager.Host;

/// <summary>Presentation only. Inventory switches retain unrelated management/recovery tools.</summary>
public static class ModuleUiPolicy
{
    sealed class Observation(HostSettings settings)
    {
        public HostSettings Settings { get; } = settings;
        public List<Action> Refresh { get; } = [];
    }

    // Neither settings nor a global event should keep a closed window (and its sessions/controls) alive.
    static readonly ConditionalWeakTable<Window, Observation> Observations = new();
    static readonly ConditionalWeakTable<HostSettings, List<WeakReference<Window>>> Windows = new();

    public static void Watch(Window window, HostSettings settings, Action refresh)
    {
        window.Dispatcher.VerifyAccess();
        if (!Observations.TryGetValue(window, out var observation))
        {
            observation = new(settings);
            Observations.Add(window, observation);
            Windows.GetOrCreateValue(settings).Add(new(window));
            window.Closed += WindowClosed;
        }
        if (!ReferenceEquals(observation.Settings, settings)) throw new InvalidOperationException();
        observation.Refresh.Add(refresh);
        refresh();
    }

    static void WindowClosed(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        window.Closed -= WindowClosed;
        Observations.Remove(window);
    }

    public static void WatchVisibility(Window window, HostSettings settings, string module, params UIElement[] controls)
    {
        var original = controls.Select(control => (Control: control, control.Visibility)).ToArray();
        Watch(window, settings, () =>
        {
            foreach (var (control, visibility) in original)
                control.Visibility = BuiltInModules.Enabled(settings, module) ? visibility : Visibility.Collapsed;
        });
    }

    /// <summary>Called on the UI thread after a successful settings save; does not restart or cancel work.</summary>
    public static void Refresh(HostSettings settings)
    {
        if (!Windows.TryGetValue(settings, out var windows)) return;
        foreach (var weak in windows.ToArray())
        {
            if (!weak.TryGetTarget(out var window) || !Observations.TryGetValue(window, out var observation))
            {
                windows.Remove(weak);
                continue;
            }
            window.Dispatcher.VerifyAccess();
            foreach (var refresh in observation.Refresh.ToArray()) refresh();
        }
    }

    public static bool CommandVisible(HostSettings settings, string command, int selectedCount = 1) => command switch
    {
        "restrictions.inventory" => BuiltInModules.Available.Any(module => module.Id == BuiltInModules.Reporting) &&
            BuiltInModules.Enabled(settings, BuiltInModules.Reporting),
        "classroom.appearance-settings" => BuiltInModules.Enabled(settings, BuiltInModules.Appearance),
        "settings.schedules" => BuiltInModules.Enabled(settings, BuiltInModules.Automation),
        "remote.programs" when selectedCount != 1 => BuiltInModules.Enabled(settings, BuiltInModules.Software),
        _ => true,
    };

    public static bool ScheduleActionVisible(HostSettings settings, string action) =>
        BuiltInModules.ActionOwner(action) is not { } owner || BuiltInModules.Enabled(settings, owner);

    public static bool ToolbarEntryVisible(HostSettings settings, string entry)
    {
        var label = ToolbarArrangement.Label(entry);
        var command = TeacherCommandCatalog.All.FirstOrDefault(item => item.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
        return command is null || CommandVisible(settings, command.Id);
    }
}
