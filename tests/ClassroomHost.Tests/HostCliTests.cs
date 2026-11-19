// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class HostCliTests
{
    static readonly SavedComputer[] Saved =
    [
        new() { Name = "PC-01", Address = "10.0.0.11", Room = "Lab A", ClientId = "id-01" },
        new() { Name = "PC-02", Address = "10.0.0.12", Room = "Lab A" },
        new() { Name = "PC-03", Address = "10.0.0.13", Room = "Lab B" },
        new() { Name = "PC-04", Address = "10.0.0.14" },
    ];

    [Fact]
    public void No_arguments_and_help_show_the_usage_text()
    {
        Assert.Equal("help", CliParser.Parse([]).Command);
        Assert.Equal("help", CliParser.Parse(["--help"]).Command);
        Assert.Equal("help", CliParser.Parse(["/?"]).Command);
    }

    [Fact]
    public void A_full_command_line_is_parsed()
    {
        var options = CliParser.Parse(["message", "--room", "Lab A", "--computer", "PC-01, PC-02", "--computer", "10.0.0.13",
            "--text", "Five minutes left", "--title", "Exam", "--seconds", "30", "--json", "--parallel", "2"]);

        Assert.Equal("message", options.Command);
        Assert.Equal("Lab A", options.Room);
        Assert.Equal(["PC-01", "PC-02", "10.0.0.13"], options.Computers);
        Assert.Equal("Five minutes left", options.Text);
        Assert.Equal("Exam", options.Title);
        Assert.Equal(30, options.Seconds);
        Assert.True(options.Json);
        Assert.Equal(2, options.Parallel);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("status")]
    [InlineData("lock")]
    [InlineData("status", "--all", "--bogus")]
    [InlineData("status", "--room")]
    [InlineData("status", "--all", "--parallel", "0")]
    [InlineData("status", "--all", "--parallel", "many")]
    [InlineData("message", "--all")]
    [InlineData("message", "--all", "--text", "   ")]
    [InlineData("power", "--all", "--yes")]
    [InlineData("power", "explode", "--all", "--yes")]
    [InlineData("power", "shutdown", "--all")]
    [InlineData("power", "sleep", "--all")]
    [InlineData("power", "restart", "--room", "Lab A")]
    [InlineData("power", "logoff", "--computer", "PC-01")]
    public void Unusable_command_lines_are_rejected(params string[] args)
    {
        Assert.Throws<CliUsageException>(() => CliParser.Parse(args));
    }

    [Fact]
    public void Power_actions_that_lose_work_need_yes_but_cancel_does_not()
    {
        var shutdown = CliParser.Parse(["power", "shutdown", "--all", "--yes", "--delay", "120"]);
        var cancel = CliParser.Parse(["power", "cancel", "--all"]);

        Assert.Equal("shutdown", shutdown.Action);
        Assert.Equal(120, shutdown.Delay);
        Assert.Equal("cancel", cancel.Action);
        Assert.Equal(60, cancel.Delay);
    }

    [Fact]
    public void Install_is_parsed_with_its_defaults_and_options()
    {
        var plain = CliParser.Parse(["install", "--room", "Lab A", "--file", "setup\\app.msi", "--yes"]);
        var full = CliParser.Parse(["install", "--all", "--file", "tool.exe", "--args", "/S /norestart", "--run-as", "USER",
            "--timeout", "600", "--delete-after", "--yes"]);

        Assert.Equal("setup\\app.msi", plain.File);
        Assert.Equal("system", plain.RunAs);
        Assert.Equal(1800, plain.Timeout);
        Assert.False(plain.DeleteAfter);
        Assert.Equal("/S /norestart", full.Arguments);
        Assert.Equal("user", full.RunAs);
        Assert.Equal(600, full.Timeout);
        Assert.True(full.DeleteAfter);
    }

    [Theory]
    [InlineData("install", "--all", "--file", "tool.exe")]
    [InlineData("install", "--all", "--yes")]
    [InlineData("install", "--file", "tool.exe", "--yes")]
    [InlineData("install", "--all", "--file", "..\\..\\client.json", "--yes")]
    [InlineData("install", "--all", "--file", "C:\\Windows\\System32\\cmd.exe", "--yes")]
    [InlineData("install", "--all", "--file", "tool.exe", "--run-as", "root", "--yes")]
    [InlineData("install", "--all", "--file", "tool.exe", "--timeout", "5", "--yes")]
    [InlineData("exam", "--all")]
    [InlineData("exam", "maybe", "--all")]
    [InlineData("exam", "on")]
    [InlineData("schedule")]
    [InlineData("schedule", "run")]
    [InlineData("schedule", "delete", "--name", "Morning lock")]
    [InlineData("schedule", "run", "--name", "Morning lock", "--all")]
    public void New_commands_reject_unusable_command_lines(params string[] args)
    {
        Assert.Throws<CliUsageException>(() => CliParser.Parse(args));
    }

    [Fact]
    public void Exam_rooms_schedules_and_schedule_run_are_parsed()
    {
        Assert.Equal("on", CliParser.Parse(["exam", "on", "--room", "Lab A"]).Action);
        Assert.Equal("off", CliParser.Parse(["exam", "OFF", "--all"]).Action);
        Assert.Equal("rooms", CliParser.Parse(["rooms", "--json"]).Command);
        Assert.Equal("schedules", CliParser.Parse(["schedules"]).Command);
        var run = CliParser.Parse(["schedule", "run", "--name", "Morning lock"]);
        Assert.Equal("run", run.Action);
        Assert.Equal("Morning lock", run.Name);
    }

    [Fact]
    public void A_schedule_becomes_the_same_command_the_Host_would_run()
    {
        var options = CliParser.Parse(["schedule", "run", "--name", "x", "--yes"]);

        var locked = CliSchedules.ToCommand(new ScheduledAction { Name = "x", Action = "lock", Title = "Break", Text = "Back at 10:15" }, options);
        Assert.Equal(("lock", "Break", "Back at 10:15"), (locked.Command, locked.Title, locked.Text));

        var shutdown = CliSchedules.ToCommand(new ScheduledAction { Name = "x", Action = "shutdown" }, options);
        Assert.Equal(("power", "shutdown", 120), (shutdown.Command, shutdown.Action, shutdown.Delay));
        Assert.Contains("shut down in 2 minutes", shutdown.Text);

        var command = CliSchedules.ToCommand(new ScheduledAction { Name = "x", Action = "command", Text = "gpupdate /force" }, options);
        Assert.Equal(("command", "gpupdate /force"), (command.Command, command.Text));
        Assert.Equal("wake", CliSchedules.ToCommand(new ScheduledAction { Name = "x", Action = "wake" }, options).Command);
    }

    [Theory]
    [InlineData("shutdown")]
    [InlineData("restart")]
    [InlineData("sleep")]
    [InlineData("command")]
    public void Schedules_that_end_sessions_or_run_commands_need_yes(string action)
    {
        var withoutYes = CliParser.Parse(["schedule", "run", "--name", "x"]);
        var schedule = new ScheduledAction { Name = "x", Action = action, Text = "dir" };

        Assert.Throws<CliUsageException>(() => CliSchedules.ToCommand(schedule, withoutYes));
        Assert.Equal("lock", CliSchedules.ToCommand(new ScheduledAction { Name = "x", Action = "lock" }, withoutYes).Command);
    }

    [Fact]
    public void Schedules_are_found_by_unique_name_and_target_their_room_or_addresses()
    {
        ScheduledAction[] schedules =
        [
            new() { Name = "Morning lock", Action = "lock", Room = "Lab A" },
            new() { Name = "Evening off", Action = "shutdown", Addresses = ["10.0.0.13", "10.0.0.14"] },
            new() { Name = "Twice", Action = "lock" }, new() { Name = "twice", Action = "unlock" },
        ];

        Assert.Equal("Lab A", CliSchedules.Find(schedules, "morning LOCK").Room);
        Assert.Throws<CliUsageException>(() => CliSchedules.Find(schedules, "Missing"));
        Assert.Throws<CliUsageException>(() => CliSchedules.Find(schedules, "Twice"));
        Assert.Equal(["PC-01", "PC-02"], CliTargets.ForSchedule(Saved, schedules[0]).Select(computer => computer.Name));
        Assert.Equal(["PC-03", "PC-04"], CliTargets.ForSchedule(Saved, schedules[1]).Select(computer => computer.Name));
        Assert.Contains("room Lab A", CliSchedules.Describe(schedules[0]));
        Assert.Contains("2 computer(s)", CliSchedules.Describe(schedules[1]));
    }

    [Fact]
    public void A_schedule_action_the_command_line_cannot_run_says_so()
    {
        var options = CliParser.Parse(["schedule", "run", "--name", "x", "--yes"]);

        var error = Assert.Throws<CliUsageException>(() =>
            CliSchedules.ToCommand(new ScheduledAction { Name = "Teleport", Action = "teleport" }, options));
        Assert.Contains("Run it from the Host", error.Message);
    }

    [Fact]
    public void List_needs_no_target_and_lists_everything()
    {
        Assert.Equal(4, CliTargets.Resolve(Saved, CliParser.Parse(["list"])).Count);
    }

    [Fact]
    public void Targets_are_selected_by_room_name_address_or_client_id()
    {
        string[] Names(params string[] args) =>
            CliTargets.Resolve(Saved, CliParser.Parse(args)).Select(computer => computer.Name).ToArray();

        Assert.Equal(["PC-01", "PC-02", "PC-03", "PC-04"], Names("status", "--all"));
        Assert.Equal(["PC-01", "PC-02"], Names("status", "--room", "lab a"));
        Assert.Equal(["PC-01", "PC-03"], Names("status", "--computer", "pc-03,ID-01"));
        Assert.Equal(["PC-02"], Names("status", "--computer", "10.0.0.12"));
        Assert.Equal(["PC-02"], Names("status", "--room", "Lab A", "--computer", "PC-02"));
    }

    [Theory]
    [InlineData("status", "--room", "Lab Z")]
    [InlineData("status", "--computer", "PC-99")]
    [InlineData("status", "--computer", "PC-01,PC-99")]
    [InlineData("status", "--room", "Lab A", "--computer", "PC-03")]
    public void A_name_that_matches_nothing_is_an_error_not_a_smaller_target(params string[] args)
    {
        var options = CliParser.Parse(args);

        Assert.Throws<CliUsageException>(() => CliTargets.Resolve(Saved, options));
    }

    [Fact]
    public void Report_lists_each_computer_and_exits_non_zero_when_any_failed()
    {
        CliResult[] mixed =
        [
            new("PC-01", "10.0.0.11", "Lab A", true, "locked"),
            new("PC-0002", "10.0.0.12", "Lab A", false, "Connection timed out"),
        ];

        Assert.Equal(
            "OK    PC-01    locked" + Environment.NewLine +
            "FAIL  PC-0002  Connection timed out" + Environment.NewLine +
            "1 succeeded, 1 failed.", CliReport.Text(mixed));
        Assert.Equal(1, CliReport.ExitCode(mixed));
        Assert.Equal(0, CliReport.ExitCode(mixed.Take(1).ToList()));

        var json = System.Text.Json.Nodes.JsonNode.Parse(CliReport.Json(mixed))!;
        Assert.Equal(1, json["succeeded"]!.GetValue<int>());
        Assert.Equal(1, json["failed"]!.GetValue<int>());
        Assert.Equal("Connection timed out", json["results"]![1]!["detail"]!.GetValue<string>());
        Assert.False(json["results"]![1]!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Bad_command_lines_exit_2_with_usage_on_standard_error()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await HostCli.RunAsync(["power", "shutdown", "--all"], output, error);

        Assert.Equal(HostCli.ExitUsage, exit);
        Assert.Equal("", output.ToString());
        Assert.Contains("needs --yes", error.ToString());
        Assert.Contains("Usage:", error.ToString());
    }

    [Fact]
    public async Task Missing_settings_exit_3_without_creating_them()
    {
        var path = Path.Combine(Path.GetTempPath(), "cm-cli-" + Guid.NewGuid().ToString("N"), "host.json");
        var error = new StringWriter();

        var exit = await HostCli.RunAsync(["status", "--all", "--settings", path], new StringWriter(), error);

        Assert.Equal(HostCli.ExitUnavailable, exit);
        Assert.Contains("No Host settings found", error.ToString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task List_reads_saved_computers_from_settings_without_contacting_them()
    {
        var directory = Directory.CreateTempSubdirectory("cm-cli-").FullName;
        try
        {
            var path = Path.Combine(directory, "host.json");
            new HostSettings
            {
                Psk = "secret",
                Computers = [new SavedComputer { Name = "PC-01", Address = "10.0.0.11", Room = "Lab A" }, new SavedComputer { Address = "10.0.0.12" }],
            }.Save(path);
            var output = new StringWriter();

            var exit = await HostCli.RunAsync(["list", "--settings", path], output, new StringWriter());

            Assert.Equal(HostCli.ExitOk, exit);
            Assert.Contains("PC-01", output.ToString());
            Assert.Contains("room Lab A", output.ToString());
            Assert.Contains("(no room)", output.ToString());
            Assert.Contains("2 succeeded, 0 failed.", output.ToString());

            var rooms = new StringWriter();
            Assert.Equal(HostCli.ExitOk, await HostCli.RunAsync(["rooms", "--settings", path], rooms, new StringWriter()));
            Assert.Contains("Lab A", rooms.ToString());
            Assert.Contains("(no room)", rooms.ToString());

            var schedules = new StringWriter();
            Assert.Equal(HostCli.ExitOk, await HostCli.RunAsync(["schedules", "--settings", path], schedules, new StringWriter()));
            // Every install has the time-sync sample, switched off.
            Assert.Matches(@"Sync the computers' time\s+disabled", schedules.ToString());
            Assert.Contains("timesync  all computers", schedules.ToString());

            var noSchedule = new StringWriter();
            Assert.Equal(HostCli.ExitUnavailable,
                await HostCli.RunAsync(["schedule", "run", "--name", "Missing", "--settings", path], new StringWriter(), noSchedule));
            Assert.Contains("no schedule named 'Missing'", noSchedule.ToString());

            var examNotConfigured = new StringWriter();
            Assert.Equal(HostCli.ExitUnavailable,
                await HostCli.RunAsync(["exam", "on", "--all", "--settings", path], new StringWriter(), examNotConfigured));
            Assert.Contains("nothing configured", examNotConfigured.ToString());

            var unknownRoom = new StringWriter();
            Assert.Equal(HostCli.ExitUnavailable,
                await HostCli.RunAsync(["status", "--room", "Lab Z", "--settings", path], new StringWriter(), unknownRoom));
            Assert.Contains("Lab Z", unknownRoom.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
