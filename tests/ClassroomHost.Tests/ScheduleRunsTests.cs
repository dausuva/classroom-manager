// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// Schedules when the Host is closed (T-214): when a schedule is due, which computers it runs on, the run record the window and the
/// background task share, the sample time-sync schedule, the Task Scheduler task and ClassroomCli schedule run-due.
/// </summary>
public sealed class ScheduleRunsTests : IDisposable
{
    static readonly DateTime Monday0815 = new(2026, 10, 26, 8, 15, 0);
    readonly string _folder = Path.Combine(Path.GetTempPath(), "cm-schedule-runs-" + Guid.NewGuid().ToString("N"));
    string RunFile => Path.Combine(_folder, "schedule-runs.json");

    public ScheduleRunsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    static ScheduledAction Schedule(string time = "08:15", bool enabled = true, params string[] days) => new()
    {
        Name = "Test", Time = time, Enabled = enabled, Days = days.Length > 0 ? [.. days] : ["Mon", "Tue", "Wed", "Thu", "Fri"], Action = "lock",
    };

    // ---- when a schedule is due ---------------------------------------------------------------

    [Theory]
    [InlineData("08:15", 0, true)]
    [InlineData("08:15", 10, true)]
    [InlineData("08:15", 11, false)]
    [InlineData("08:16", 0, false)]
    [InlineData("8:15", 3, true)]
    [InlineData("25:00", 0, false)]
    [InlineData("soon", 0, false)]
    public void A_schedule_is_due_from_its_time_for_ten_minutes(string time, int minutesLater, bool due) =>
        Assert.Equal(due, ScheduleRuns.IsDue(Schedule(time), Monday0815.AddMinutes(minutesLater)));

    [Fact]
    public void A_disabled_schedule_or_another_weekday_is_not_due()
    {
        Assert.False(ScheduleRuns.IsDue(Schedule(enabled: false), Monday0815));
        Assert.False(ScheduleRuns.IsDue(Schedule("08:15", true, "Tue"), Monday0815));
        Assert.True(ScheduleRuns.IsDue(Schedule("08:15", true, "mon"), Monday0815));
    }

    [Fact]
    public void A_schedule_runs_on_all_computers_one_room_or_the_named_ones()
    {
        SavedComputer[] saved =
        [
            new() { Address = "10.0.0.1", Room = "410" }, new() { Address = "10.0.0.2", Room = "410" }, new() { Address = "10.0.0.3", Room = "411" },
        ];
        Assert.Equal(3, ScheduleRuns.Targets(saved, new ScheduledAction { AllComputers = true, Room = "410" }).Count);
        Assert.Equal(["10.0.0.1", "10.0.0.2"], ScheduleRuns.Targets(saved, new ScheduledAction { Room = "410" }).Select(c => c.Address));
        Assert.Equal(["10.0.0.3"], ScheduleRuns.Targets(saved, new ScheduledAction { Addresses = ["10.0.0.3"] }).Select(c => c.Address));
        Assert.Empty(ScheduleRuns.Targets(saved, new ScheduledAction()));
    }

    // ---- the run record -----------------------------------------------------------------------

    [Fact]
    public void A_schedule_is_claimed_once_a_day()
    {
        Assert.True(ScheduleRuns.TryClaim("a", Monday0815, RunFile));
        Assert.False(ScheduleRuns.TryClaim("a", Monday0815.AddMinutes(1), RunFile));
        Assert.True(ScheduleRuns.TryClaim("b", Monday0815, RunFile));
        Assert.True(ScheduleRuns.TryClaim("a", Monday0815.AddDays(1), RunFile));
        Assert.Equal("2026-10-27", ScheduleRuns.LastRuns(RunFile)["a"]);
    }

    [Fact]
    public void When_the_window_and_the_task_claim_at_once_only_one_runs_the_schedule()
    {
        var wins = 0;
        Parallel.For(0, 16, _ => { if (ScheduleRuns.TryClaim("together", Monday0815, RunFile)) Interlocked.Increment(ref wins); });
        Assert.Equal(1, wins);
    }

    [Fact]
    public void A_damaged_record_refuses_execution_and_a_missing_one_is_empty()
    {
        Assert.Empty(ScheduleRuns.LastRuns(RunFile));
        File.WriteAllText(RunFile, "not json");
        Assert.Throws<System.Text.Json.JsonException>(() => ScheduleRuns.TryClaim("a", Monday0815, RunFile));
        Assert.Equal("not json", File.ReadAllText(RunFile));
    }

    // ---- the sample schedule ------------------------------------------------------------------

    [Fact]
    public void Every_install_gets_the_time_sync_sample_switched_off_for_all_computers_every_day()
    {
        // A settings file that does not exist yet is what a new install starts from.
        var fresh = HostSettings.Load(Path.Combine(_folder, "new-install.json"));
        var sample = Assert.Single(fresh.Schedules, schedule => schedule.Action == "timesync");
        Assert.False(sample.Enabled);
        Assert.True(sample.AllComputers);
        Assert.True(sample.CatchUp);
        Assert.True(ScheduleRuns.CatchesUp(sample));
        Assert.Equal(7, sample.Days.Count);
        Assert.Equal("Sync the computers' time", sample.Name);
        Assert.Equal("timesync", CliSchedules.ToCommand(sample, CliParser.Parse(["schedule", "run", "--name", "x"])).Command);
    }

    [Fact]
    public void The_sample_is_added_once_and_a_deleted_one_stays_deleted()
    {
        var path = Path.Combine(_folder, "host.json");
        var settings = new HostSettings();
        settings.Save(path);
        var loaded = HostSettings.Load(path);
        Assert.Single(loaded.Schedules, schedule => schedule.Action == "timesync");

        loaded.Schedules.Clear();
        loaded.Save(path);
        Assert.Empty(HostSettings.Load(path).Schedules);
    }

    [Fact]
    public void A_host_that_already_syncs_time_gets_no_second_schedule()
    {
        var path = Path.Combine(_folder, "older.json");
        File.WriteAllText(path, """{ "schedules": [ { "name": "Clocks", "action": "timesync", "room": "410" } ] }""");
        var loaded = HostSettings.Load(path);
        Assert.Equal("Clocks", Assert.Single(loaded.Schedules, schedule => schedule.Action == "timesync").Name);
        Assert.Equal(3, loaded.ScheduleDefaultsVersion);
        Assert.False(loaded.Schedules[0].CatchUp);
    }

    [Fact]
    public void The_switch_is_off_by_default_and_never_copied_to_a_second_host() =>
        Assert.False(new HostSettings().RunSchedulesWhenClosed);

    [Fact]
    public void A_sample_added_before_catching_up_existed_now_catches_up_but_a_teachers_own_schedule_does_not()
    {
        var path = Path.Combine(_folder, "version1.json");
        File.WriteAllText(path, """
            { "scheduleDefaultsVersion": 1, "schedules": [
              { "id": "s", "name": "Sync the computers' time", "action": "timesync", "allComputers": true, "time": "08:15" },
              { "id": "t", "name": "Clocks of 410", "action": "timesync", "room": "410", "time": "08:00" } ] }
            """);
        var loaded = HostSettings.Load(path);
        Assert.True(loaded.Schedules.Single(schedule => schedule.Id == "s").CatchUp);
        Assert.False(loaded.Schedules.Single(schedule => schedule.Id == "t").CatchUp);
    }

    // ---- catching up --------------------------------------------------------------------------

    [Theory]
    [InlineData("timesync", true, true)]
    [InlineData("command", true, true)]
    [InlineData("timesync", false, false)]
    [InlineData("lock", true, false)]
    [InlineData("message", true, false)]
    [InlineData("shutdown", true, false)]
    [InlineData("wake", true, false)]
    public void Only_actions_harmless_at_any_hour_catch_up(string action, bool catchUp, bool catchesUp) =>
        Assert.Equal(catchesUp, ScheduleRuns.CatchesUp(new ScheduledAction { Action = action, CatchUp = catchUp }));

    [Theory]
    [InlineData("2026-10-26 08:15", "2026-10-26 08:15")]   // Monday at the time
    [InlineData("2026-10-26 13:40", "2026-10-26 08:15")]   // later that day: still Monday's run
    [InlineData("2026-10-27 07:00", "2026-10-26 08:15")]   // before Tuesday's time: Monday's run is the latest
    [InlineData("2026-10-31 09:00", "2026-10-30 08:15")]   // Saturday, weekdays only: Friday's run
    [InlineData("2026-11-02 08:14", "2026-10-30 08:15")]   // Monday a minute early: still Friday's
    public void The_latest_run_is_the_last_time_on_a_chosen_day(string now, string expected) =>
        Assert.Equal(DateTime.ParseExact(expected, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ScheduleRuns.LatestOccurrence(Schedule(), DateTime.ParseExact(now, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));

    [Fact]
    public void A_disabled_schedule_or_one_without_days_has_no_latest_run()
    {
        Assert.Null(ScheduleRuns.LatestOccurrence(Schedule(enabled: false), Monday0815));
        Assert.Null(ScheduleRuns.LatestOccurrence(new ScheduledAction { Days = [], Time = "08:15" }, Monday0815));
        Assert.Null(ScheduleRuns.LatestOccurrence(Schedule("noon"), Monday0815));
    }

    string ComputersFile => Path.Combine(_folder, "schedule-computers.json");

    [Fact]
    public void Each_computer_is_tried_until_it_has_had_the_run_and_then_left_alone()
    {
        var t0 = new DateTime(2026, 10, 26, 6, 15, 0, DateTimeKind.Utc);
        const string run = "2026-10-26 08:15";
        Assert.Equal(["pc1", "pc2", "pc3"], ScheduleRuns.ClaimDue("s", run, ["pc1", "pc2", "pc3"], t0, ComputersFile));
        // pc1 and pc2 answered; pc3 was off.
        ScheduleRuns.MarkDone("s", run, ["pc1", "pc2"], ComputersFile);
        Assert.Equal(2, ScheduleRuns.DoneCount("s", run, ["pc1", "pc2", "pc3"], ComputersFile));

        // A minute later nothing is tried: pc3 waits a few minutes before the next try.
        Assert.Empty(ScheduleRuns.ClaimDue("s", run, ["pc1", "pc2", "pc3"], t0.AddMinutes(1), ComputersFile));
        Assert.Equal(["pc3"], ScheduleRuns.ClaimDue("s", run, ["pc1", "pc2", "pc3"], t0 + ScheduleRuns.RetryAfter, ComputersFile));
        ScheduleRuns.MarkDone("s", run, ["pc3"], ComputersFile);
        Assert.Empty(ScheduleRuns.ClaimDue("s", run, ["pc1", "pc2", "pc3"], t0.AddHours(5), ComputersFile));

        // The next day's run starts afresh for every computer; another schedule has its own record.
        Assert.Equal(3, ScheduleRuns.ClaimDue("s", "2026-10-27 08:15", ["pc1", "pc2", "pc3"], t0.AddDays(1), ComputersFile).Count);
        Assert.Single(ScheduleRuns.ClaimDue("other", run, ["pc1"], t0, ComputersFile));
    }

    [Fact]
    public void The_window_and_the_task_never_try_the_same_computer_at_once()
    {
        var t0 = DateTime.UtcNow;
        var claims = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 12, _ => { foreach (var key in ScheduleRuns.ClaimDue("s", "run", ["a", "b", "c"], t0, ComputersFile)) claims.Add(key); });
        Assert.Equal(["a", "b", "c"], claims.Order());
    }

    [Fact]
    public void A_computer_is_known_by_its_client_id_or_else_its_address() =>
        Assert.Equal(["abc", "10.0.0.9"], new[] { new SavedComputer { ClientId = "ABC", Address = "10.0.0.1" }, new SavedComputer { Address = "10.0.0.9" } }
            .Select(ScheduleRuns.ComputerKey));

    [Fact]
    public async Task A_computer_that_fails_is_left_for_later_and_its_error_is_logged_once()
    {
        var settings = new HostSettings { Psk = "synthetic-catch-up-key" };
        settings.Computers.Add(new SavedComputer { Address = "pc-01.demo.invalid", Name = "PC-01", Port = 9 });
        var schedule = new ScheduledAction
        {
            Id = Guid.NewGuid().ToString("N"), Name = "Clocks", Action = "timesync", CatchUp = true, AllComputers = true,
            Time = "00:00", Days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"],
        };
        settings.Schedules.Add(schedule);
        var lines = new List<string>();

        var first = await HostCli.RunCatchUpAsync(settings, CliParser.Parse(["schedule", "run-due", "--yes"]), DateTime.Now, lines.Add);
        Assert.False(Assert.Single(first).Ok);
        // Refused before connecting (its certificate is not approved here): worth one line, not one at every try.
        Assert.Contains("(error code 030001)", Assert.Single(lines));
        var run = ScheduleRuns.OccurrenceKey(ScheduleRuns.LatestOccurrence(schedule, DateTime.Now)!.Value);
        Assert.Equal(0, ScheduleRuns.DoneCount(schedule.Id, run, ["pc-01.demo.invalid"]));
        // Tried a moment ago: not again until a few minutes have passed.
        Assert.Empty(await HostCli.RunCatchUpAsync(settings, CliParser.Parse(["schedule", "run-due", "--yes"]), DateTime.Now, lines.Add));
    }

    [Fact]
    public void The_same_error_is_noted_once_per_run_and_a_new_one_again()
    {
        ScheduleRuns.ClaimDue("s", "run", ["pc"], DateTime.UtcNow, ComputersFile);
        Assert.True(ScheduleRuns.NoteFailure("s", "run", "pc", "Refused (error code 040001)", ComputersFile));
        Assert.False(ScheduleRuns.NoteFailure("s", "run", "pc", "Refused (error code 040001)", ComputersFile));
        Assert.True(ScheduleRuns.NoteFailure("s", "run", "pc", "Other (error code 0A0001)", ComputersFile));
        // A computer not being tried in this run has nothing to note.
        Assert.False(ScheduleRuns.NoteFailure("s", "another run", "pc", "x", ComputersFile));
    }

    [Theory]
    [InlineData("The computer cannot be reached (error code 010002)", true)]
    [InlineData("Connection refused (error code 010001)", true)]
    [InlineData("Wrong key (error code 020001)", false)]
    [InlineData("No code at all", false)]
    public void A_connection_error_counts_as_not_reachable(string shown, bool unreachable) =>
        Assert.Equal(unreachable, ErrorClassifier.IsUnreachable(shown));

    [Fact]
    public void A_catch_up_schedule_is_marked_in_the_command_line_list() =>
        Assert.Contains("catches up", CliSchedules.Describe(new ScheduledAction { Name = "x", Action = "timesync", CatchUp = true, AllComputers = true }));

    // ---- the Task Scheduler task --------------------------------------------------------------

    [Fact]
    public void The_task_runs_the_windowed_host_every_minute_only_while_the_user_is_signed_in()
    {
        var args = BackgroundSchedules.CreateArguments("Teacher", @"C:\Program Files\Classroom Manager\ClassroomHost.exe");
        Assert.Equal(@"\Classroom Manager\Host schedules (Teacher)", args[args.IndexOf("/TN") + 1]);
        Assert.Equal("\"C:\\Program Files\\Classroom Manager\\ClassroomHost.exe\" --cli schedule run-due --yes", args[args.IndexOf("/TR") + 1]);
        Assert.Equal(("MINUTE", "1"), (args[args.IndexOf("/SC") + 1], args[args.IndexOf("/MO") + 1]));
        // Interactive only: no stored password; /F replaces the task when the program moved.
        Assert.Contains("/IT", args);
        Assert.Contains("/F", args);
        Assert.DoesNotContain("/RU", args);
        Assert.DoesNotContain("/RP", args);
        Assert.Equal(["/Delete", "/TN", @"\Classroom Manager\Host schedules (Teacher)", "/F"], BackgroundSchedules.DeleteArguments("Teacher"));
        Assert.Equal(@"\Classroom Manager\Host schedules (a_b)", BackgroundSchedules.TaskName(@"a\b"));
    }

    [Fact]
    public void The_task_starts_the_windowed_host_beside_the_command_line_program()
    {
        var host = Path.Combine(_folder, "ClassroomHost.exe");
        var cli = Path.Combine(_folder, "ClassroomCli.exe");
        Assert.Equal(cli, BackgroundSchedules.HostProgram(cli));
        File.WriteAllText(host, "");
        Assert.Equal(host, BackgroundSchedules.HostProgram(cli));
        Assert.Equal(host, BackgroundSchedules.HostProgram(host));
    }

    // ---- ClassroomCli ---------------------------------------------------------------------------

    [Theory]
    [InlineData("schedule", "run-due", "--name", "x")]
    [InlineData("schedule", "run-due", "--all")]
    [InlineData("schedule", "later")]
    [InlineData("timesync")]
    public void Unusable_schedule_and_timesync_command_lines_are_refused(params string[] args) =>
        Assert.Throws<CliUsageException>(() => CliParser.Parse(args));

    [Fact]
    public void Run_due_and_timesync_are_parsed()
    {
        var due = CliParser.Parse(["schedule", "run-due", "--yes", "--json"]);
        Assert.Equal(("schedule", "run-due", true, true), (due.Command, due.Action, due.Yes, due.Json));
        var sync = CliParser.Parse(["timesync", "--room", "410"]);
        Assert.Equal(("timesync", "410"), (sync.Command, sync.Room));
        Assert.Contains("schedule  run-due", CliParser.Usage);
        Assert.Contains("timesync", CliParser.Usage);
    }

    [Fact]
    public void A_schedule_for_all_computers_is_described_so() =>
        Assert.Contains("all computers", CliSchedules.Describe(new ScheduledAction { Name = "x", AllComputers = true }));

    [Fact]
    public async Task Run_due_runs_a_due_schedule_once_and_writes_it_to_the_activity_log()
    {
        var path = Path.Combine(_folder, "due.json");
        var now = DateTime.Now;
        var settings = new HostSettings();
        settings.Schedules.Add(new ScheduledAction
        {
            Name = "Due now " + Guid.NewGuid().ToString("N")[..6], Action = "lock", Room = "Nowhere",
            Time = now.ToString("HH:mm", CultureInfo.InvariantCulture), Days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"],
        });
        settings.Save(path);

        var first = new StringWriter();
        Assert.Equal(HostCli.ExitOk, await HostCli.RunAsync(["schedule", "run-due", "--settings", path], first, new StringWriter()));
        Assert.Contains("1 schedule(s) ran on no computer", first.ToString());
        var second = new StringWriter();
        Assert.Equal(HostCli.ExitOk, await HostCli.RunAsync(["schedule", "run-due", "--settings", path], second, new StringWriter()));
        Assert.Contains("No schedule is due", second.ToString());

        var log = File.ReadAllText(Path.Combine(ActivityLogFile.DefaultDirectory, ActivityLogFile.FileName(DateTime.Now)));
        Assert.Contains($"Schedule \"{settings.Schedules[^1].Name}\" (Host closed): Lock screen (with message) on 0 computer(s)", log);
    }

    [Fact]
    public async Task Run_due_does_nothing_when_schedules_are_off_in_the_host()
    {
        var path = Path.Combine(_folder, "off.json");
        var settings = new HostSettings { RunSchedules = false };
        settings.Schedules.Add(new ScheduledAction { Name = "Off", Action = "lock", Room = "x", Time = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
            Days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"] });
        settings.Save(path);
        var output = new StringWriter();
        Assert.Equal(HostCli.ExitOk, await HostCli.RunAsync(["schedule", "run-due", "--settings", path], output, new StringWriter()));
        Assert.Contains("turned off", output.ToString());
        Assert.DoesNotContain(settings.Schedules[0].Id, ScheduleRuns.LastRuns().Keys);
    }

    [Fact]
    public async Task Run_due_skips_a_risky_schedule_without_yes_and_says_why()
    {
        var path = Path.Combine(_folder, "risky.json");
        var settings = new HostSettings();
        settings.Schedules.Add(new ScheduledAction { Name = "Shut down " + Guid.NewGuid().ToString("N")[..6], Action = "shutdown", Room = "x",
            Time = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture), Days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"] });
        settings.Save(path);
        var output = new StringWriter();
        Assert.Equal(HostCli.ExitFailures, await HostCli.RunAsync(["schedule", "run-due", "--settings", path], output, new StringWriter()));
        Assert.Contains("--yes", output.ToString());
    }
}
