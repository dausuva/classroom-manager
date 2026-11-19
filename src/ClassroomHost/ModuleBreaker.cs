// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// Remembers the modules the broker stopped because a run kept asking for things the module may not have (three refused calls in one run).
/// A stopped module is treated as switched off until the person ticks it again in Host settings, or the Host is restarted. This is not the
/// person's switch: it is never saved, so a module that misbehaved once is not locked out for good.
/// </summary>
public static class ModuleBreaker
{
    static readonly Dictionary<string, string> Tripped = new(StringComparer.Ordinal);

    public static void Trip(string moduleId, string reason)
    {
        lock (Tripped) Tripped.TryAdd(moduleId, reason);
    }

    public static bool IsTripped(string moduleId)
    {
        lock (Tripped) return Tripped.ContainsKey(moduleId);
    }

    /// <summary>Why the module was stopped, or null.</summary>
    public static string? Reason(string moduleId)
    {
        lock (Tripped) return Tripped.GetValueOrDefault(moduleId);
    }

    public static void Clear(string moduleId)
    {
        lock (Tripped) Tripped.Remove(moduleId);
    }
}
