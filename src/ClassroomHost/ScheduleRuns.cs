// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;

namespace ClassroomManager.Host;

/// <summary>
/// When a schedule runs, and the record that it ran today. The Host window and the background task (ClassroomCli schedule run-due) both
/// claim a schedule here before running it, so it runs once a day even when both are active. The record is a file of its own: the window
/// keeps its settings in memory and saves them whole, so a date kept in the settings could be put back to yesterday by the window.
/// </summary>
public static class ScheduleRuns
{
    /// <summary>How late a schedule may still start: a PC that wakes or a task that starts a few minutes late still runs it.</summary>
    public const int LateMinutes = 10;

    public static string FilePath => Path.Combine(HostSettings.DataFolder, "schedule-runs.json");

    /// <summary>True when the schedule should run now: enabled, on today's weekday, and its time passed less than <see cref="LateMinutes"/> ago.</summary>
    public static bool IsDue(ScheduledAction schedule, DateTime now)
    {
        if (!schedule.Enabled || !schedule.Days.Contains(now.DayOfWeek.ToString()[..3], StringComparer.OrdinalIgnoreCase)) return false;
        if (!TimeSpan.TryParse(schedule.Time, System.Globalization.CultureInfo.InvariantCulture, out var at) || at < TimeSpan.Zero || at >= TimeSpan.FromDays(1)) return false;
        var due = now.Date + at;
        return now >= due && (now - due).TotalMinutes <= LateMinutes;
    }

    /// <summary>The saved computers a schedule runs on: all of them, one room, or the computers it names.</summary>
    public static List<SavedComputer> Targets(IReadOnlyList<SavedComputer> saved, ScheduledAction schedule) =>
        schedule.AllComputers ? saved.ToList()
        : schedule.Room.Length > 0 ? saved.Where(computer => computer.Room == schedule.Room).ToList()
        : saved.Where(computer => schedule.Addresses.Contains(computer.Address, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Records that the schedule runs today and returns true, or returns false when it already ran today. The file is held exclusively
    /// while it is read and written, so two runners asking at once get one true between them.
    /// </summary>
    public static bool TryClaim(string scheduleId, DateTime now, string? path = null)
    {
        path ??= FilePath;
        var today = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return JsonRecordFile.Use(path, () => new Dictionary<string, string>(StringComparer.Ordinal), runs =>
        {
            if (runs.TryGetValue(scheduleId, out var last) && last == today) return false;
            runs[scheduleId] = today;
            return true;
        }, write: true);
    }

    /// <summary>The day each schedule last ran (yyyy-MM-dd), by schedule id.</summary>
    public static Dictionary<string, string> LastRuns(string? path = null)
        => JsonRecordFile.Use(path ?? FilePath, () => new Dictionary<string, string>(StringComparer.Ordinal),
            runs => new Dictionary<string, string>(runs, StringComparer.Ordinal), write: false);

    /// <summary>The second sample: a weekly look for waiting Windows updates on every computer (it installs nothing), switched off.</summary>
    public static ScheduledAction SampleUpdateCheck() => new()
    {
        Name = "Check for Windows updates",
        Enabled = false,
        Time = "08:30",
        Days = ["Mon"],
        Action = "updatescan",
        AllComputers = true,
        CatchUp = true,
    };

    /// <summary>The third sample: a monthly backup of the students' work (first Monday, at noon), switched off.</summary>
    public static ScheduledAction SampleBackup() => new()
    {
        Name = "Back up student work",
        Enabled = false,
        Time = "12:00",
        Days = ["Mon"],
        Monthly = true,
        Action = "backup",
        AllComputers = true,
        CatchUp = true,
    };

    /// <summary>The sample schedule every install gets once, switched off: the computers' clocks set to this PC's, every day.</summary>
    public static ScheduledAction SampleTimeSync() => new()
    {
        Name = "Sync the computers' time",
        Enabled = false,
        Time = "08:15",
        Days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"],
        Action = "timesync",
        AllComputers = true,
        CatchUp = true,
    };

    // ---- catching up: each computer once per run, as soon as it is online ----------------------------------------------------

    /// <summary>The actions that may run late on a computer that was off: harmless at any hour. Lock, message and power never catch up.</summary>
    public static readonly IReadOnlySet<string> CatchUpActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "timesync", "command", "updatescan", "updateinstall", "backup" };

    /// <summary>Actions that only make sense per computer, so they always catch up: an update check or a backup is owed to every computer.</summary>
    public static readonly IReadOnlySet<string> AlwaysCatchUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "updatescan", "updateinstall", "backup" };

    /// <summary>How long a computer that did not answer is left before it is tried again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    public static string ComputersFilePath => Path.Combine(HostSettings.DataFolder, "schedule-computers.json");

    /// <summary>True when the schedule catches up computers that were off at its time.</summary>
    public static bool CatchesUp(ScheduledAction schedule) =>
        AlwaysCatchUp.Contains(schedule.Action) || schedule.CatchUp && CatchUpActions.Contains(schedule.Action);

    /// <summary>
    /// The schedule's latest time at or before <paramref name="now"/>: today's time once it has passed, otherwise the last earlier day it
    /// runs on (up to a week back). Null when it is disabled, has no day, or has no valid time. A computer that was off then is due until the
    /// next time comes.
    /// </summary>
    public static DateTime? LatestOccurrence(ScheduledAction schedule, DateTime now)
    {
        if (!schedule.Enabled || schedule.Days.Count == 0) return null;
        if (!TimeSpan.TryParse(schedule.Time, System.Globalization.CultureInfo.InvariantCulture, out var at) || at < TimeSpan.Zero || at >= TimeSpan.FromDays(1)) return null;
        // Monthly: only the first of the chosen weekdays in each month (the first Monday, say), so look back a month and a bit.
        for (int back = 0; back <= (schedule.Monthly ? 40 : 7); back++)
        {
            var day = now.Date.AddDays(-back);
            var occurrence = day + at;
            if (occurrence <= now && schedule.Days.Contains(day.DayOfWeek.ToString()[..3], StringComparer.OrdinalIgnoreCase) &&
                (!schedule.Monthly || day.Day <= 7)) return occurrence;
        }
        return null;
    }

    /// <summary>The name of one run in the record: its date and time.</summary>
    public static string OccurrenceKey(DateTime occurrence) => occurrence.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>How a saved computer is known in the record: its Client id, or its address before it reported one.</summary>
    public static string ComputerKey(SavedComputer computer) => (computer.ClientId.Length > 0 ? computer.ClientId : computer.Address).ToLowerInvariant();

    /// <summary>One computer's state for one schedule.</summary>
    public sealed class ComputerRun
    {
        public string Occurrence { get; set; } = "";
        public bool Done { get; set; }
        /// <summary>Persisted immediately before a non-idempotent command is dispatched. Only acknowledged success clears it.</summary>
        public bool OutcomeUnknown { get; set; }
        public string Owner { get; set; } = "";
        public DateTime TriedUtc { get; set; }
        /// <summary>The last error of this run, so the same error goes to the log once, not at every try.</summary>
        public string LastError { get; set; } = "";
    }

    /// <summary>Notes a computer's error for this run; true when it is new (first error, or a different one) and so worth a log line.</summary>
    public static bool NoteFailure(string scheduleId, string occurrence, string computerKey, string error, string? path = null)
    {
        var isNew = false;
        Update(path ?? ComputersFilePath, runs =>
        {
            if (!runs.TryGetValue(scheduleId + "|" + computerKey, out var run) || run.Occurrence != occurrence) return;
            isNew = run.LastError != error;
            run.LastError = error;
        });
        return isNew;
    }

    /// <summary>
    /// Picks the computers to try now for one run of a schedule and marks them as being tried: not done yet for this run, and not tried in
    /// the last <see cref="RetryAfter"/>. Held exclusively, so the Host window and the background task never try the same computer at once.
    /// </summary>
    public static List<string> ClaimDue(string scheduleId, string occurrence, IEnumerable<string> computerKeys, DateTime nowUtc, string? path = null)
    {
        var claimed = new List<string>();
        Update(path ?? ComputersFilePath, runs =>
        {
            foreach (var key in computerKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var id = scheduleId + "|" + key;
                if (runs.TryGetValue(id, out var run) && run.Occurrence == occurrence && (run.Done || run.OutcomeUnknown || nowUtc - run.TriedUtc < RetryAfter)) continue;
                runs[id] = new ComputerRun { Occurrence = occurrence, TriedUtc = nowUtc };
                claimed.Add(key);
            }
        });
        return claimed;
    }

    /// <summary>Records that the computers finished this run of the schedule.</summary>
    public static void MarkDone(string scheduleId, string occurrence, IEnumerable<string> computerKeys, string? path = null) =>
        Update(path ?? ComputersFilePath, runs =>
        {
            foreach (var key in computerKeys)
                if (runs.TryGetValue(scheduleId + "|" + key, out var run) && run.Occurrence == occurrence)
                { run.Done = true; run.OutcomeUnknown = false; run.Owner = ""; }
        });

    public static void MarkDispatch(string scheduleId, string occurrence, string key, string owner, string? path = null) =>
        Update(path ?? ComputersFilePath, runs =>
        {
            var id = scheduleId + "|" + key;
            if (!runs.TryGetValue(id, out var run) || run.Occurrence != occurrence)
                throw new InvalidOperationException("A scheduled command must be claimed before dispatch.");
            run.OutcomeUnknown = true;
            run.Owner = owner;
        });

    public static int UnknownCount(string scheduleId, string occurrence, IEnumerable<string> computerKeys, string? path = null)
    {
        var runs = ReadComputers(path ?? ComputersFilePath);
        return computerKeys.Count(key => runs.TryGetValue(scheduleId + "|" + key, out var run) && run.Occurrence == occurrence && run.OutcomeUnknown);
    }

    /// <summary>How many of the computers finished the run (for the Schedules window).</summary>
    public static int DoneCount(string scheduleId, string occurrence, IEnumerable<string> computerKeys, string? path = null)
    {
        var runs = ReadComputers(path ?? ComputersFilePath);
        return computerKeys.Count(key => runs.TryGetValue(scheduleId + "|" + key, out var run) && run.Occurrence == occurrence && run.Done);
    }

    static Dictionary<string, ComputerRun> ReadComputers(string path)
        => JsonRecordFile.Use(path, () => new Dictionary<string, ComputerRun>(StringComparer.OrdinalIgnoreCase),
            runs => new Dictionary<string, ComputerRun>(runs, StringComparer.OrdinalIgnoreCase), write: false);

    static void Update(string path, Action<Dictionary<string, ComputerRun>> change)
    {
        JsonRecordFile.Use(path, () => new Dictionary<string, ComputerRun>(StringComparer.OrdinalIgnoreCase), runs =>
        {
            change(runs);
            return true;
        }, write: true);
    }
}
