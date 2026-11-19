// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>
/// How often one Teacher key may send the commands that can do harm if a script or a stolen key repeats them (T-190): each listed command has a
/// number it may be sent per minute, counted per key across all of that key's connections. Admin keys are not limited. The defaults are far above
/// what a lesson needs (every Client sees one request per teacher action) and are set in <c>client.json</c> under <c>teacherCommandLimits</c>
/// (0 = no limit for that command).
/// </summary>
public sealed class KeyCommandLimiter
{
    public static IReadOnlyDictionary<string, int> DefaultPerMinute { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [Cmd.PowerAction] = 3,
        [Cmd.ExamModeSet] = 6,
        [Cmd.WallpaperSet] = 6,
        [Cmd.MessageShow] = 12,
        [Cmd.WebsiteOpen] = 12,
        [Cmd.ProgramOpen] = 12,
        [Cmd.LockSet] = 20,
    };

    readonly IReadOnlyDictionary<string, int> _perMinute;
    readonly TimeProvider _time;
    readonly object _lock = new();
    readonly Dictionary<(string Key, string Command), Queue<DateTimeOffset>> _recent = new();

    public KeyCommandLimiter(IReadOnlyDictionary<string, int>? overrides = null, TimeProvider? time = null)
    {
        var merged = new Dictionary<string, int>(DefaultPerMinute, StringComparer.Ordinal);
        if (overrides is not null)
            foreach (var pair in overrides) merged[pair.Key] = pair.Value;
        _perMinute = merged;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>True when the command may be sent now with this key; the use is counted. Only Teacher and lower roles are limited.</summary>
    public bool TryTake(string keyId, OperatorRole role, string command)
    {
        if (role >= OperatorRole.Admin) return true;
        if (!_perMinute.TryGetValue(command, out var limit) || limit <= 0) return true;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (!_recent.TryGetValue((keyId, command), out var queue)) _recent[(keyId, command)] = queue = new Queue<DateTimeOffset>();
            while (queue.Count > 0 && now - queue.Peek() >= TimeSpan.FromMinutes(1)) queue.Dequeue();
            if (queue.Count >= limit) return false;
            queue.Enqueue(now);
            return true;
        }
    }
}
