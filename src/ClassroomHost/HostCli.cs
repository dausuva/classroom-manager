// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Maintenance;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Raised for a command line the tool cannot act on; the message is shown with the usage text.</summary>
public sealed class CliUsageException(string message) : Exception(message);

/// <summary>A parsed <c>ClassroomCli</c> command line.</summary>
public sealed record CliOptions
{
    public IReadOnlyList<string> CertificatePins { get; init; } = [];
    public bool ClearTrust { get; init; }
    public string Command { get; init; } = "";
    public string Action { get; init; } = "";
    public IReadOnlyList<string> Computers { get; init; } = [];
    public string Room { get; init; } = "";
    public bool All { get; init; }
    public bool Json { get; init; }
    public bool Yes { get; init; }
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public int Seconds { get; init; }
    public int Delay { get; init; } = 60;
    public int Parallel { get; init; } = 8;
    public string SettingsPath { get; init; } = "";
    /// <summary>install: the installer's path inside this Host's temporary uploads.</summary>
    public string File { get; init; } = "";
    /// <summary>install: the installer's own switches.</summary>
    public string Arguments { get; init; } = "";
    /// <summary>install: "system" (elevated, machine-wide) or "user" (the signed-in student).</summary>
    public string RunAs { get; init; } = Levels.System;
    public int Timeout { get; init; } = 1800;
    public bool DeleteAfter { get; init; }
    /// <summary>schedule run: the schedule's name.</summary>
    public string Name { get; init; } = "";
    /// <summary>update: the new client program on this computer, and whether to apply it even when it is not newer.</summary>
    public string ClientFile { get; init; } = "";
    public bool Force { get; init; }
    /// <summary>trust-check: look at the network too, make the safe fixes, approve one computer, print the router table.</summary>
    public bool Live { get; init; }
    public bool Fix { get; init; }
    public bool Approve { get; init; }
    public string Check { get; init; } = "";
    public bool Reservations { get; init; }
    /// <summary>trust-check: a folder (or one file) of enrolment files written by Classroom Client Health on the computers.</summary>
    public string FromFiles { get; init; } = "";
    /// <summary>trust-check: list the backups of the settings that the trust tools made, or put one back (by number or path).</summary>
    public bool ListBackups { get; init; }
    public string Restore { get; init; } = "";
    public bool WithComputers { get; init; }

    public bool HasTarget => All || Room.Length > 0 || Computers.Count > 0;
}

public static class CliParser
{
    public const string Usage = """
        ClassroomCli - scripted control of the computers saved in this Windows user's Classroom Host.

        Usage:
          ClassroomCli list      [--room <name>] [--json]
          ClassroomCli rooms     [--json]
          ClassroomCli schedules [--json]
          ClassroomCli modules   [--json]
          ClassroomCli modules   install --name <package folder>
          ClassroomCli modules   remove|rollback --name <module id>
          ClassroomCli appearance presets [--json]
          ClassroomCli appearance export --name <preset> --file <path>
          ClassroomCli appearance import --file <path>
          ClassroomCli appearance apply <target> --name <preset>
          ClassroomCli status    <target> [--json]
          ClassroomCli lock      <target> [--title <text>] [--text <text>]
          ClassroomCli unlock    <target>
          ClassroomCli message   <target> --text <text> [--title <text>] [--seconds <n>]
          ClassroomCli power     <shutdown|restart|logoff|sleep|cancel> <target> [--delay <seconds>] [--text <text>] --yes
          ClassroomCli wake      <target>
          ClassroomCli timesync  <target>
          ClassroomCli updatescan <target>
          ClassroomCli updateinstall <target> --yes
          ClassroomCli backup    <target>
          ClassroomCli exam      <on|off> <target>
          ClassroomCli install   <target> --file <path in Temporary uploads> [--args <switches>] [--run-as system|user]
                                 [--timeout <seconds>] [--delete-after] --yes
          ClassroomCli update    <target> --client <signed ClassroomClient.exe on this computer> [--force] --yes
          ClassroomCli security  [--json]
          ClassroomCli derive-keys <target> --yes
          ClassroomCli schedule  run --name <schedule name> [--yes]
          ClassroomCli schedule  run-due [--yes] [--json]
          ClassroomCli toolbar-reset
          ClassroomCli trust --computer <one saved computer> --certificate-sha256 <independently verified leaf SHA-256> --yes
                             [--certificate-sha256 <replacement SHA-256 for overlap>]
          ClassroomCli trust --computer <one saved computer> --clear --yes
          ClassroomCli trust-check [--live] [--fix --yes] [--reservations] [--json]
          ClassroomCli trust-check --approve --computer <one saved computer> --check <first 20+ hex digits shown by Client Health> --yes
          ClassroomCli trust-check --from-files <folder of enrolment files> [--yes]
          ClassroomCli trust-check --list-backups
          ClassroomCli trust-check --restore <number or path from --list-backups> [--with-computers] [--yes]

        Target (combine to narrow; list, rooms, schedules and schedule take none):
          --all                 every saved computer
          --room <name>         computers assigned to that room
          --computer <name>     a computer by name, address, or client id; repeat or separate with commas

        Options:
          --json                machine-readable results on standard output
          --parallel <n>        computers contacted at once (default 8)
          --yes                 required for power shutdown, restart and logoff, for install, and for
                                schedules that shut down, restart, sleep or run a command
          --data-folder <dir>   use the settings and keys in that folder instead of this Windows user's own (first or
                                last argument; also the CLASSROOM_DATA_FOLDER variable, or a ClassroomHost.portable file
                                beside the program, which means the folder ClassroomHostData beside it)

        modules lists every module of this Host: built in or installed as a package, whether it is on or off in Host settings,
        the services it asks for and, for a package that was refused, the reasons. It changes nothing and contacts no computer.
        Exit code 1 when a package was refused.
        modules install checks a package folder completely (manifest, every file's SHA-256, signatures) and stages it; remove and
        rollback stage their change. Staged changes apply when the Host or ClassroomCli next starts, before any module code is loaded.
        The replaced version is kept for one rollback.
        security asks every saved computer for its protocol, key and settings and prints one line per property of the lab (TLS,
        approved certificates, a key per computer, up-to-date Clients, discovery, security events) with the command that fixes
        what is not as it should be. It changes nothing. Exit code 0 when nothing is a problem or a warning, 1 otherwise.
        derive-keys gives each target computer its own key, derived from the lab key and the computer's client id, using the
        ordinary key change (the new key is tried before the old one is let go and the old one is put back if it fails). A computer
        stolen from the room then opens nothing else. This Host keeps working with them; another Host reaches them only if it holds
        the same lab key. Computers already on their own key are left alone.
        Close the Host window first: an open window keeps its own list of computers and saves it over the new key id, then tries the
        lab key first, which the computer now refuses; repeated refusals make the computer ignore this PC for a few minutes.
        exam uses the websites, apps and device controls saved under Exam mode settings in the Host, and
        records the computers in the Host's settings: an open Host window shows the exam within half a minute.
        install runs an installer already uploaded with Temporary uploads in the Host. update uploads a
        signed Classroom Client build and has each computer replace its own client with it, as Update
        client in the Host does; the client checks the signature and refuses a build that is not newer
        unless --force is given. schedule run performs a saved schedule now, whatever its time, so
        Windows Task Scheduler can run schedules while the Host is closed. schedule run-due performs every enabled schedule whose
        time came in the last 10 minutes and that has not run today; "Run schedules when the Host is closed" in Host settings has
        Task Scheduler run it every minute. timesync sets the computers' clocks to this PC's. updateinstall downloads and installs every waiting Windows update and never restarts the computer (it says when a restart is needed); updatescan lists the
        Windows updates waiting on each computer and installs nothing. backup copies what changed in each computer's Recent files
        folders since its last backup (everything the first time) to Backups in the Recent files folder on this PC; a student must
        be signed in. toolbar-reset puts every tab of the
        Host's toolbar back as the program lays it out, for when an arrangement left it unusable; close the
        Host first.

        Exit codes: 0 every computer succeeded; 1 at least one failed; 2 bad command line; 3 no settings or no matching computers.
        """;

    static readonly string[] Commands =
        ["list", "rooms", "schedules", "modules", "status", "lock", "unlock", "message", "power", "wake", "timesync", "updatescan", "updateinstall", "backup", "appearance", "exam", "install", "update", "derive-keys", "security", "schedule", "toolbar-reset", "trust", "trust-check", "help"];
    static readonly string[] NoTargetCommands = ["help", "list", "rooms", "schedules", "modules", "schedule", "toolbar-reset", "trust-check", "security"];
    static readonly string[] PowerActions = ["shutdown", "restart", "logoff", "sleep", "cancel"];
    static readonly string[] ExamActions = ["on", "off"];

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new CliOptions { Command = "help" };
        var command = args[0].TrimStart('-', '/').ToLowerInvariant();
        if (command is "h" or "?") command = "help";
        if (!Commands.Contains(command)) throw new CliUsageException($"Unknown command '{args[0]}'.");

        var options = new CliOptions { Command = command };
        var computers = new List<string>();
        var certificatePins = new List<string>();
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            string Value() => ++i < args.Count ? args[i] : throw new CliUsageException($"{arg} needs a value.");
            int Number(int minimum, int maximum)
            {
                var text = Value();
                return int.TryParse(text, out var number) && number >= minimum && number <= maximum
                    ? number
                    : throw new CliUsageException($"{arg} must be a whole number from {minimum} to {maximum}.");
            }

            switch (arg.ToLowerInvariant())
            {
                case "--certificate-sha256": certificatePins.Add(Value()); break;
                case "--clear": options = options with { ClearTrust = true }; break;
                case "--live": options = options with { Live = true }; break;
                case "--fix": options = options with { Fix = true }; break;
                case "--approve": options = options with { Approve = true }; break;
                case "--reservations": options = options with { Reservations = true }; break;
                case "--from-files": options = options with { FromFiles = Value().Trim().Trim('"') }; break;
                case "--list-backups": options = options with { ListBackups = true }; break;
                case "--restore": options = options with { Restore = Value().Trim().Trim('"') }; break;
                case "--with-computers": options = options with { WithComputers = true }; break;
                case "--check": options = options with { Check = Value().Trim() }; break;
                case "--all": options = options with { All = true }; break;
                case "--json": options = options with { Json = true }; break;
                case "--yes": options = options with { Yes = true }; break;
                case "--delete-after": options = options with { DeleteAfter = true }; break;
                case "--force": options = options with { Force = true }; break;
                case "--client": options = options with { ClientFile = Value().Trim().Trim('"') }; break;
                case "--room": options = options with { Room = Value().Trim() }; break;
                case "--title": options = options with { Title = Value() }; break;
                case "--text": options = options with { Text = Value() }; break;
                case "--settings": options = options with { SettingsPath = Value() }; break;
                case "--file": options = options with { File = Value().Trim() }; break;
                case "--args": options = options with { Arguments = Value() }; break;
                case "--name": options = options with { Name = Value().Trim() }; break;
                case "--seconds": options = options with { Seconds = Number(0, 86400) }; break;
                case "--delay": options = options with { Delay = Number(0, 86400) }; break;
                case "--timeout": options = options with { Timeout = Number(30, 86400) }; break;
                case "--parallel": options = options with { Parallel = Number(1, 64) }; break;
                case "--run-as":
                    var runAs = Value().ToLowerInvariant();
                    if (runAs is not (Levels.System or Levels.User)) throw new CliUsageException("--run-as must be system or user.");
                    options = options with { RunAs = runAs };
                    break;
                case "--computer":
                    computers.AddRange(Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                default:
                    if (command is "power" or "exam" or "schedule" or "modules" or "appearance" && options.Action.Length == 0 && !arg.StartsWith('-'))
                    {
                        options = options with { Action = arg.ToLowerInvariant() };
                        break;
                    }
                    throw new CliUsageException($"Unknown option '{arg}'.");
            }
        }
        options = options with { Computers = computers, CertificatePins = certificatePins };
        Validate(options);
        return options;
    }

    static void Validate(CliOptions options)
    {
        if (options.Command != "trust" && (options.ClearTrust || options.CertificatePins.Count > 0))
            throw new CliUsageException("Certificate enrollment options are only valid with 'trust'.");
        if (options.Command == "trust")
        {
            if (!options.Yes || options.All || options.Room.Length > 0 || options.Computers.Count != 1)
                throw new CliUsageException("'trust' requires exactly one --computer and --yes; room/all enrollment is not allowed.");
            if (options.ClearTrust ? options.CertificatePins.Count != 0 :
                options.CertificatePins.Count is < 1 or > 2 || options.CertificatePins.Any(pin => !TlsEndpointTrust.IsFingerprint(pin)))
                throw new CliUsageException("'trust' requires one or two exact 64-hex certificate SHA-256 values, or --clear alone.");
            return;
        }
        if (options.Command == "trust-check")
        {
            if (options.Approve && (!options.Yes || options.Computers.Count != 1 || options.All || options.Room.Length > 0 || options.Check.Length == 0))
                throw new CliUsageException("'trust-check --approve' needs exactly one --computer, --check <fingerprint start> and --yes.");
            if (!options.Approve && options.Check.Length > 0) throw new CliUsageException("--check is only for --approve.");
            if (options.Fix && !options.Yes) throw new CliUsageException("'trust-check --fix' needs --yes.");
            if ((options.ListBackups || options.Restore.Length > 0) && (options.Approve || options.Fix || options.Live || options.Reservations || options.FromFiles.Length > 0 || options.Computers.Count > 0 || (options.ListBackups && options.Restore.Length > 0)))
                throw new CliUsageException("'trust-check --list-backups' and '--restore' stand alone.");
            if (options.WithComputers && options.Restore.Length == 0) throw new CliUsageException("--with-computers is only for --restore.");
            if (options.FromFiles.Length > 0 && (options.Approve || options.Fix || options.Live || options.Reservations || options.Computers.Count > 0))
                throw new CliUsageException("'trust-check --from-files' stands alone: it reads the files and shows (or, with --yes, makes) the approvals they allow.");
            return;
        }
        if (options.Command == "schedule")
        {
            if (options.Action is not ("run" or "run-due")) throw new CliUsageException("'schedule' needs the action run or run-due.");
            if (options.HasTarget) throw new CliUsageException($"'schedule {options.Action}' uses the schedules' own computers; do not give a target.");
            if (options.Action == "run-due")
            {
                if (options.Name.Length > 0) throw new CliUsageException("'schedule run-due' runs every schedule that is due; use 'schedule run --name' for one.");
                return;
            }
            if (options.Name.Length == 0) throw new CliUsageException("'schedule run' needs --name.");
            return;
        }
        if (options.Command == "modules")
        {
            if (options.Action.Length == 0) return;
            if (options.Action is not ("install" or "remove" or "rollback")) throw new CliUsageException("'modules' takes no action, or install, remove or rollback.");
            if (options.Name.Length == 0) throw new CliUsageException($"'modules {options.Action}' needs --name {(options.Action == "install" ? "<the package folder>" : "<the module id>")}.");
            if (options.HasTarget) throw new CliUsageException($"'modules {options.Action}' changes this Host only; do not give a target.");
            return;
        }
        if (options.Command == "appearance")
        {
            if (options.Action is not ("presets" or "export" or "import" or "apply")) throw new CliUsageException("'appearance' needs the action presets, export, import or apply.");
            if (options.Action is "export" or "apply" && options.Name.Length == 0) throw new CliUsageException($"'appearance {options.Action}' needs --name <preset>.");
            if (options.Action is "export" or "import" && options.File.Length == 0) throw new CliUsageException($"'appearance {options.Action}' needs --file <path>.");
            if (options.Action != "apply" && options.HasTarget) throw new CliUsageException($"'appearance {options.Action}' works on this Host's saved presets; do not give a target.");
            if (options.Action == "apply" && !options.HasTarget) throw new CliUsageException("'appearance apply' needs a target: --all, --room <name>, or --computer <name>.");
            return;
        }
        if (NoTargetCommands.Contains(options.Command)) return;
        if (!options.HasTarget)
            throw new CliUsageException($"'{options.Command}' needs a target: --all, --room <name>, or --computer <name>.");
        switch (options.Command)
        {
            case "message" when options.Text.Trim().Length == 0:
                throw new CliUsageException("'message' needs --text.");
            case "exam" when !ExamActions.Contains(options.Action):
                throw new CliUsageException("'exam' needs on or off.");
            case "install":
                if (options.File.Length == 0) throw new CliUsageException("'install' needs --file.");
                try { StagingNamespace.NormalizeRelativePath(options.File); }
                catch (ArgumentException) { throw new CliUsageException("--file must be a path inside Temporary uploads, without a drive letter or '..'."); }
                // It runs a program on every target, elevated by default, so a script has to say so explicitly.
                if (!options.Yes) throw new CliUsageException("'install' needs --yes to confirm.");
                break;
            case "update":
                if (options.ClientFile.Length == 0) throw new CliUsageException("'update' needs --client with the path of the new ClassroomClient.exe.");
                // Every target replaces its own client and restarts its service.
                if (!options.Yes) throw new CliUsageException("'update' needs --yes to confirm.");
                break;
            case "updateinstall":
                if (!options.Yes) throw new CliUsageException("'updateinstall' needs --yes to confirm."); // it slows the computer while it runs
                break;
            case "derive-keys":
                // Every target changes the key it accepts, so a script has to say so explicitly.
                if (!options.Yes) throw new CliUsageException("'derive-keys' needs --yes to confirm.");
                break;
            case "power":
                if (!PowerActions.Contains(options.Action))
                    throw new CliUsageException("'power' needs an action: shutdown, restart, logoff, sleep, or cancel.");
                // Students lose unsaved work, so an unattended script has to say so explicitly.
                if (options.Action != "cancel" && !options.Yes)
                    throw new CliUsageException($"'power {options.Action}' needs --yes to confirm.");
                break;
        }
    }
}

public static class CliUpdate
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The SHA-256 of the client program, which every computer checks against what it received; read once per file.</summary>
    public static string Sha256(string file) => Known.GetOrAdd(Path.GetFullPath(file), path =>
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    });
}

public static class CliTargets
{
    /// <summary>
    /// The saved computers a command applies to. --room and --computer narrow each other; a name that
    /// matches nothing is an error rather than a silently smaller target.
    /// </summary>
    public static List<SavedComputer> Resolve(IReadOnlyList<SavedComputer> saved, CliOptions options)
    {
        IEnumerable<SavedComputer> selected = saved;
        if (options.Room.Length > 0)
        {
            selected = selected.Where(computer => computer.Room.Equals(options.Room, StringComparison.OrdinalIgnoreCase));
            if (!selected.Any()) throw new CliUsageException($"No saved computer is assigned to room '{options.Room}'.");
        }
        if (options.Computers.Count > 0)
        {
            var pool = selected.ToList();
            var unknown = options.Computers.Where(name => !pool.Any(computer => Matches(computer, name))).ToList();
            if (unknown.Count > 0)
                throw new CliUsageException("No saved computer matches: " + string.Join(", ", unknown) +
                                            (options.Room.Length > 0 ? $" (in room '{options.Room}')" : "") + ".");
            selected = pool.Where(computer => options.Computers.Any(name => Matches(computer, name)));
        }
        return selected.ToList();
    }

    static bool Matches(SavedComputer computer, string name) =>
        computer.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        computer.Address.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        (computer.ClientId.Length > 0 && computer.ClientId.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The computers a saved schedule applies to: its room, or else its listed addresses.</summary>
    public static List<SavedComputer> ForSchedule(IReadOnlyList<SavedComputer> saved, ScheduledAction schedule) =>
        ScheduleRuns.Targets(saved, schedule);
}

/// <summary>Turns a saved schedule into the command the CLI would run for it, with the Host's own defaults.</summary>
public static class CliSchedules
{
    static readonly string[] NeedsConfirmation = ["shutdown", "restart", "sleep", "command", "updateinstall"];

    public static ScheduledAction Find(IReadOnlyList<ScheduledAction> schedules, string name)
    {
        var matches = schedules.Where(schedule => schedule.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliUsageException($"There is no schedule named '{name}'. Run 'ClassroomCli schedules' to list them."),
            _ => throw new CliUsageException($"{matches.Count} schedules are named '{name}'; rename them in the Host so the name is unique."),
        };
    }

    public static CliOptions ToCommand(ScheduledAction schedule, CliOptions options)
    {
        if (NeedsConfirmation.Contains(schedule.Action) && !options.Yes)
            throw new CliUsageException($"Schedule '{schedule.Name}' performs '{schedule.Action}'; add --yes to confirm.");
        return schedule.Action switch
        {
            "lock" => options with { Command = "lock", Title = schedule.Title, Text = schedule.Text },
            "unlock" => options with { Command = "unlock" },
            "message" => options with { Command = "message", Title = schedule.Title, Text = schedule.Text, Seconds = 0 },
            "wake" => options with { Command = "wake" },
            "shutdown" => options with { Command = "power", Action = "shutdown", Delay = 120, Text = "This computer will shut down in 2 minutes. Please save your work." },
            "restart" => options with { Command = "power", Action = "restart", Delay = 120, Text = "This computer will restart in 2 minutes. Please save your work." },
            "sleep" => options with { Command = "power", Action = "sleep", Delay = 0, Text = "" },
            "command" => options with { Command = "command", Text = schedule.Text },
            "timesync" => options with { Command = "timesync" },
            "updatescan" => options with { Command = "updatescan" },
            "updateinstall" => options with { Command = "updateinstall" },
            "backup" => options with { Command = "backup" },
            "appearance" => options with { Command = "appearance", Action = "apply", Name = schedule.Text.Trim() },
            _ => throw new CliUsageException($"Schedule '{schedule.Name}' performs '{schedule.Action}', which ClassroomCli cannot run yet. Run it from the Host."),
        };
    }

    public static string Describe(ScheduledAction schedule) =>
        $"{(schedule.Enabled ? "enabled " : "disabled")}  {schedule.Time}  {string.Join(",", schedule.Days)}  {schedule.Action}  " +
        (schedule.AllComputers ? "all computers" : schedule.Room.Length > 0 ? $"room {schedule.Room}" : $"{schedule.Addresses.Count} computer(s)") +
        (ScheduleRuns.CatchesUp(schedule) ? "  catches up" : "");
}

public sealed record CliResult(string Computer, string Address, string Room, bool Ok, string Detail);

public static class CliReport
{
    public static int ExitCode(IReadOnlyCollection<CliResult> results) => results.All(result => result.Ok) ? 0 : 1;

    public static string Text(IReadOnlyCollection<CliResult> results)
    {
        var width = results.Count == 0 ? 0 : results.Max(result => result.Computer.Length);
        var lines = results.Select(result =>
            $"{(result.Ok ? "OK  " : "FAIL")}  {result.Computer.PadRight(width)}  {result.Detail}".TrimEnd());
        var failed = results.Count(result => !result.Ok);
        return string.Join(Environment.NewLine, lines.Append($"{results.Count - failed} succeeded, {failed} failed."));
    }

    public static string Json(IReadOnlyCollection<CliResult> results) => JsonSerializer.Serialize(new
    {
        succeeded = results.Count(result => result.Ok),
        failed = results.Count(result => !result.Ok),
        results = results.Select(result => new
        {
            computer = result.Computer, address = result.Address, room = result.Room, ok = result.Ok, detail = result.Detail,
        }),
    }, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>
/// Runs Host actions without the window, for scheduled tasks and scripts. It reads the same settings
/// and protected keys as the Host of the current Windows user and saves them only for exam (the computers
/// an exam covers) and toolbar-reset (which refuses while the Host is open), so it can run while the Host
/// is open. Bulk work goes through <see cref="BulkRunner"/>, the same code the window uses.
/// </summary>
public static class HostCli
{
    public const int ExitOk = 0;
    public const int ExitFailures = 1;
    public const int ExitUsage = 2;
    public const int ExitUnavailable = 3;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        CliOptions options;
        try { options = CliParser.Parse(args); }
        catch (CliUsageException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine();
            error.WriteLine(CliParser.Usage);
            return ExitUsage;
        }
        if (options.Command == "help")
        {
            output.WriteLine(CliParser.Usage);
            return ExitOk;
        }

        var path = options.SettingsPath.Length > 0 ? options.SettingsPath : HostSettings.PathOnDisk;
        if (!File.Exists(path))
        {
            error.WriteLine($"No Host settings found at {path}. Start Classroom Host once as this Windows user and add computers first.");
            return ExitUnavailable;
        }

        HostSettings settings;
        List<SavedComputer> targets;
        List<CliResult>? listing = null;
        try
        {
            settings = HostSettings.Load(path);
            switch (options.Command)
            {
                case "trust":
                    if (Path.GetFullPath(path).Equals(Path.GetFullPath(HostSettings.PathOnDisk), StringComparison.OrdinalIgnoreCase) && HostWindowOpen())
                        throw new CliUsageException("Close Classroom Host before changing endpoint trust.");
                    var approvedTargets = CliTargets.Resolve(settings.Computers, options);
                    if (approvedTargets.Count != 1) throw new CliUsageException("Endpoint trust must resolve to exactly one saved computer.");
                    var approvedTarget = approvedTargets[0];
                    var approvedPort = approvedTarget.Port > 0 ? approvedTarget.Port : settings.TcpPort;
                    settings.TrustedTlsEndpoints ??= [];
                    // What the address holds for another network card stays (its computer may be there): only this
                    // computer's own approvals, and approvals nobody has claimed at its address, are replaced.
                    settings.EnrollTrust(approvedTarget.Address, approvedPort, approvedTarget.Mac, options.ClearTrust ? [] : options.CertificatePins);
                    settings.Save(path);
                    output.WriteLine("Endpoint trust saved from explicitly supplied fingerprints. No network certificate was fetched or automatically approved.");
                    return ExitOk;
                case "trust-check":
                    return await TrustCheckCli.RunAsync(settings, path, options, output, HostWindowOpen());
                case "security":
                    return await SecurityCli.RunAsync(settings, options, output);
                case "toolbar-reset":
                    // The one command that writes the settings: an open Host would write its own toolbar back over it.
                    if (Path.GetFullPath(path).Equals(Path.GetFullPath(HostSettings.PathOnDisk), StringComparison.OrdinalIgnoreCase) && HostWindowOpen())
                        throw new CliUsageException("Close Classroom Host first: an open Host keeps its own toolbar and would save it again.");
                    settings.ToolbarLayouts.Clear();
                    settings.Save(path);
                    output.WriteLine("The toolbar is back as Classroom Manager lays it out. Arrange toolbar and Interface settings are shown in both modes.");
                    return ExitOk;
                case "rooms":
                    listing = settings.Computers
                        .GroupBy(computer => computer.Room.Length > 0 ? computer.Room : "(no room)", StringComparer.OrdinalIgnoreCase)
                        .OrderBy(room => room.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(room => new CliResult(room.Key, "", room.Key, true, $"{room.Count()} computer(s)")).ToList();
                    targets = [];
                    break;
                case "schedules":
                    listing = settings.Schedules
                        .Select(schedule => new CliResult(schedule.Name, "", schedule.Room, true, CliSchedules.Describe(schedule))).ToList();
                    targets = [];
                    break;
                case "appearance" when options.Action == "presets":
                    listing = CliAppearance.List(settings);
                    targets = [];
                    break;
                case "appearance" when options.Action == "export":
                    output.WriteLine(CliAppearance.Export(settings, options.Name, options.File));
                    return ExitOk;
                case "appearance" when options.Action == "import":
                    // Writes the settings, like toolbar-reset: an open Host would write its own presets back over it.
                    if (Path.GetFullPath(path).Equals(Path.GetFullPath(HostSettings.PathOnDisk), StringComparison.OrdinalIgnoreCase) && HostWindowOpen())
                        throw new CliUsageException("Close Classroom Host first: an open Host keeps its own presets and would save them again.");
                    output.WriteLine(CliAppearance.Import(settings, options.File));
                    settings.Save(path);
                    return ExitOk;
                case "appearance":
                    CliAppearance.Find(settings, options.Name);
                    targets = CliTargets.Resolve(settings.Computers, options);
                    break;
                case "modules" when options.Action.Length > 0:
                    return CliModules.Change(options.Action, options.Name, output, options.Json);
                case "modules":
                    listing = CliModules.List(BuiltInModules.Registry, settings);
                    targets = [];
                    break;
                case "schedule" when options.Action == "run-due":
                    return await RunDueAsync(settings, options, output);
                case "schedule":
                    var schedule = CliSchedules.Find(settings.Schedules, options.Name);
                    options = CliSchedules.ToCommand(schedule, options);
                    targets = CliTargets.ForSchedule(settings.Computers, schedule);
                    break;
                default:
                    targets = CliTargets.Resolve(settings.Computers, options);
                    break;
            }
            if (options.Command == "update" && !File.Exists(options.ClientFile))
                throw new CliUsageException($"The client program was not found: {options.ClientFile}");
            if (options.Command == "exam" && options.Action == "on" && settings.ExamModeDomains.Count == 0 && settings.ExamModeApps.Count == 0 &&
                ExamDeviceControls.Describe(settings.ExamModeMuteAudio, settings.ExamModeDisableCamera, settings.ExamModeBlockUsbStorage).Length == 0)
                throw new CliUsageException("Exam mode has nothing configured. Set websites, apps or device controls under Exam mode settings in the Host first.");
        }
        catch (CliUsageException ex)
        {
            error.WriteLine(ex.Message);
            return ExitUnavailable;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.InnerException is null ? ex.Message : $"{ex.Message} {ex.InnerException.Message}");
            return ExitUnavailable;
        }

        if (listing is null && targets.Count == 0)
        {
            error.WriteLine("No saved computers match.");
            return ExitUnavailable;
        }

        var results = listing ?? (options.Command == "list"
            ? targets.Select(computer => new CliResult(Name(computer), computer.Address, computer.Room, true,
                computer.Room.Length > 0 ? $"{computer.Address}  room {computer.Room}" : $"{computer.Address}  (no room)")).ToList()
            : await RunOnAsync(targets, settings, options));

        if (listing is { Count: 0 }) output.WriteLine(options.Command == "rooms" ? "No computers are saved." : "No schedules are saved.");
        else output.WriteLine(options.Json ? CliReport.Json(results) : CliReport.Text(results));
        return CliReport.ExitCode(results);
    }

    /// <summary>
    /// Runs every schedule that is due now and has not run today (claimed in ScheduleRuns, so the Host window and this never both run it),
    /// and writes what it did to the Host's activity log, where the Log view shows it.
    /// </summary>
    static async Task<int> RunDueAsync(HostSettings settings, CliOptions options, TextWriter output)
    {
        if (!settings.RunSchedules || !BuiltInModules.Enabled(settings, BuiltInModules.Automation))
        {
            output.WriteLine("Schedules are turned off in this Host (Host settings > Run scheduled actions).");
            return ExitOk;
        }
        var log = new ActivityLogFile(knownSecrets: () => settings.CurrentHostKeys().Select(key => key.Psk).Concat(settings.PreviousPsks));
        var now = DateTime.Now;
        var results = new List<CliResult>();
        var workflow = new ScheduleWorkflow(ScheduleRuns.FilePath, ScheduleRuns.ComputersFilePath);
        var started = await workflow.RunTimedAsync(BuiltInModules.AdmittedSchedules(settings), now, async (schedule, token) =>
        {
            CliOptions command;
            try { command = CliSchedules.ToCommand(schedule, options with { Name = schedule.Name }); }
            catch (CliUsageException ex)
            {
                log.Write(DateTime.Now, $"Schedule \"{schedule.Name}\" was not run: {ex.Message}");
                results.Add(new CliResult(schedule.Name, "", schedule.Room, false, ex.Message));
                return;
            }
            var targets = ScheduleRuns.Targets(settings.Computers, schedule);
            log.Write(DateTime.Now, $"Schedule \"{schedule.Name}\" (Host closed): {SchedulesWindow.ActionLabel(schedule.Action)} on {targets.Count} computer(s)");
            if (targets.Count == 0) return;
            foreach (var result in await RunOnAsync(targets, settings, command, ct: token))
            {
                log.Write(DateTime.Now, $"  {(result.Ok ? "OK  " : "FAIL")}  {result.Computer}: {result.Detail}");
                results.Add(result with { Computer = $"{schedule.Name}: {result.Computer}" });
            }
        }, (schedule, ex) =>
        {
            log.Write(DateTime.Now, $"Schedule \"{schedule.Name}\" was not run: {ex.Message}");
            results.Add(new CliResult(schedule.Name, "", schedule.Room, false, ex.Message));
        });
        var caughtUp = await RunCatchUpAsync(settings, options, now, line => log.Write(DateTime.Now, line));
        results.AddRange(caughtUp);
        if (results.Count == 0)
        {
            output.WriteLine(started == 0 ? "No schedule is due." : $"{started} schedule(s) ran on no computer.");
            return ExitOk;
        }
        output.WriteLine(options.Json ? CliReport.Json(results) : CliReport.Text(results));
        return CliReport.ExitCode(results);
    }

    /// <summary>
    /// Schedules that catch up (ScheduleRuns.CatchesUp): for the latest run of each, the computers that have not had it yet and were not
    /// tried in the last few minutes are tried now. The Host window and the background task both call this; the shared record keeps them
    /// from trying the same computer at once. A computer that does not answer is tried again later; a run that worked is not repeated.
    /// Successes and failures other than "not reachable" go to <paramref name="log"/>.
    /// </summary>
    public static Task<List<CliResult>> RunCatchUpAsync(HostSettings settings, CliOptions options, DateTime now, Action<string> log,
        CancellationToken ct = default) =>
        new ScheduleWorkflow(ScheduleRuns.FilePath, ScheduleRuns.ComputersFilePath).RunCatchUpAsync(BuiltInModules.AdmittedSchedules(settings), now,
            schedule => ScheduleRuns.Targets(settings.Computers, schedule),
            (schedule, computers, dispatch, token) => RunOnAsync(computers, settings,
                CliSchedules.ToCommand(schedule, options with { Name = schedule.Name, Yes = true }),
                client => dispatch((client.ClientId.Length > 0 ? client.ClientId : client.Address).ToLowerInvariant()), token), log, ct);

    static bool HostWindowOpen()
    {
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("ClassroomHost"))
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.SessionId == session && process.MainWindowHandle != IntPtr.Zero) return true;
                }
                catch (InvalidOperationException) { }
            }
        return false;
    }

    static string Name(SavedComputer computer) => computer.Name.Length > 0 ? computer.Name : computer.Address;

    static async Task<List<CliResult>> RunOnAsync(List<SavedComputer> targets, HostSettings settings, CliOptions options,
        Action<ClientInfo>? beforeAction = null, CancellationToken ct = default)
    {
        if (options.Command == "backup" && !BuiltInModules.Enabled(settings, BuiltInModules.StudentWork))
            return targets.Select(computer => new CliResult(Name(computer), computer.Address, computer.Room, false, BuiltInModules.Disabled)).ToList();
        if (options.Command == "appearance" && !BuiltInModules.Enabled(settings, BuiltInModules.Appearance))
            return targets.Select(computer => new CliResult(Name(computer), computer.Address, computer.Room, false, BuiltInModules.Disabled)).ToList();
        // Wake-on-LAN goes to computers that are off, so there is no connection to open.
        if (options.Command == "wake")
        {
            var woken = new List<CliResult>();
            foreach (var computer in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await WakeOnLan.SendAsync(computer.Mac, computer.Address, ct);
                    woken.Add(new CliResult(Name(computer), computer.Address, computer.Room, true, "Wake-on-LAN packet sent"));
                }
                catch (Exception ex) { woken.Add(new CliResult(Name(computer), computer.Address, computer.Room, false, ex.Message)); }
            }
            return woken;
        }

        var clients = targets.Select(computer => new ClientInfo
        {
            Address = computer.Address, Name = Name(computer), ClientId = computer.ClientId,
            Mac = ComputerIdentity.NormalizeMac(computer.Mac), Port = computer.Port, Room = computer.Room, KeyId = computer.KeyId,
        }).ToList();
        var waitingResults = await CliLicenceLimit.ApplyAsync(clients, options.Command, ct); var outcomes = new ConcurrentDictionary<ClientInfo, (bool Ok, string Detail)>();
        await BulkRunner.RunAsync(clients, settings,
            async (client, session) =>
            {
                beforeAction?.Invoke(client);
                outcomes[client] = (true, await RunOneAsync(client, session, settings, options, ct));
            },
            options.Parallel, ct,
            new BulkRunCallbacks { Failed = (client, ex) => outcomes[client] = (false, ErrorClassifier.Describe(ex)) });
        if (options.Command == "derive-keys")
        {
            // Remember the new key id, so that the next connection starts with the key the computers accept now.
            DeviceKeyMigration.Record(settings, clients.Where(client => outcomes.TryGetValue(client, out var result) && result.Ok));
            settings.Save(options.SettingsPath.Length > 0 ? options.SettingsPath : HostSettings.PathOnDisk);
        }
        if (options.Command == "exam")
        {
            // The Host window keeps the list of computers an exam covers; tell it what was done here,
            // so it shows the exam, re-applies it to a computer that restarts, and can turn it off.
            ExamModeScope.Record(settings, options.Action == "on",
                clients.Where(client => outcomes.TryGetValue(client, out var result) && result.Ok).Select(client => (client.ClientId, client.Address)));
            settings.Save(options.SettingsPath.Length > 0 ? options.SettingsPath : HostSettings.PathOnDisk);
        }
        return clients.Select(client =>
        {
            var outcome = outcomes.TryGetValue(client, out var found) ? found : (Ok: false, Detail: "No result was recorded.");
            return new CliResult(client.Name, client.Address, client.Room, outcome.Ok, outcome.Detail);
        }).Concat(waitingResults).ToList();
    }

    static async Task<string> RunOneAsync(ClientInfo client, ClientSession session, HostSettings settings, CliOptions options, CancellationToken ct = default)
    {
        switch (options.Command)
        {
            case "status":
                var info = await session.CallAsync(Cmd.Ping);
                var user = info["userName"]?.GetValue<string>() ?? "";
                return $"online; user {(user.Length > 0 ? user : "(nobody signed in)")}; " +
                       $"protocol {info["version"]?.GetValue<string>()}; client {ClassroomManager.Shared.ReleaseVersions.Display(info["appVersion"]?.GetValue<string>())}";
            case "lock":
                await session.CallAtAsync(Levels.User, Cmd.LockSet, new JsonObject
                {
                    ["title"] = options.Title.Length > 0 ? options.Title : "Screen locked",
                    ["message"] = options.Text.Length > 0 ? options.Text : "Please look at the teacher.",
                });
                return "locked";
            case "unlock":
                await session.CallAtAsync(Levels.User, Cmd.LockClear);
                return "unlocked";
            case "message":
                await session.CallAtAsync(Levels.User, Cmd.MessageShow, new JsonObject
                {
                    ["title"] = options.Title.Length > 0 ? options.Title : "Message from your teacher",
                    ["text"] = options.Text,
                    ["seconds"] = options.Seconds,
                });
                return "message shown";
            case "power":
                await session.CallAsync(Cmd.PowerAction, new JsonObject
                {
                    ["action"] = options.Action, ["delaySec"] = options.Delay, ["message"] = options.Text,
                });
                return options.Action == "cancel" ? "pending power action cancelled" : $"{options.Action} requested";
            case "timesync":
                // This PC's clock, stamped right before sending, as Sync time in the Host does.
                var synced = await session.CallAsync(Cmd.TimeSync, new JsonObject { ["mode"] = "host", ["utcTicks"] = DateTime.UtcNow.Ticks }, ct);
                return synced["note"]?.GetValue<string>() ?? "time set";
            case "updatescan":
                var (maintenance, machine) = ModuleRuns.OpenMaintenance(session, client);
                var titles = MaintenanceOperations.ReadTitles(await maintenance.ScanAsync(machine, includeOptional: null, ct));
                return titles.Count == 0 ? "no updates waiting"
                    : $"{titles.Count} update(s) waiting: {string.Join("; ", titles.Take(3))}{(titles.Count > 3 ? "; ..." : "")}";
            case "updateinstall":
                return await CliUpdateCommands.InstallWindowsUpdatesAsync(session, client, ct);
            case "backup":
                return await StudentBackup.RunAsync(session, client, settings, ct);
            case "appearance":
                return await CliAppearance.ApplyAsync(client, session, settings, options.Name, ct);
            case "command":
                var run = await session.CallAsync(Cmd.CmdRun, new JsonObject { ["shell"] = "cmd", ["command"] = options.Text, ["timeoutSec"] = 300 }, ct);
                return $"command finished with exit code {run["exitCode"]?.GetValue<int>()}";
            case "exam":
                return await SetExamModeAsync(session, settings, options.Action == "on");
            case "update":
                return await CliUpdateCommands.SendClientAsync(session, settings, options);
            case "derive-keys":
                return await DeviceKeyMigration.ApplyAsync(client, settings, session);
            case "install":
                var installed = await session.CallAtAsync(options.RunAs, Cmd.InstallRun, new JsonObject
                {
                    ["path"] = StagingNamespace.RemotePath(settings, options.File),
                    ["args"] = options.Arguments,
                    ["timeoutSec"] = options.Timeout,
                });
                var exitCode = installed["exitCode"]?.GetValue<int>();
                if (!options.DeleteAfter) return $"installed (exit code {exitCode})";
                await session.CallAsync(Cmd.StagingDelete, new JsonObject
                {
                    ["namespace"] = StagingNamespace.Id(settings),
                    ["relativePath"] = options.File,
                });
                return $"installed (exit code {exitCode}); staged file deleted";
            default:
                throw new InvalidOperationException($"Unsupported command '{options.Command}'.");
        }
    }

    /// <summary>Applies or removes the Exam mode overlay saved in the Host's settings, device controls included.</summary>
    static async Task<string> SetExamModeAsync(ClientSession session, HostSettings settings, bool enable)
    {
        if (!enable)
        {
            await session.CallAsync(Cmd.ExamModeClear);
            return "Exam mode off";
        }
        await session.CallAsync(Cmd.ExamModeSet, new JsonObject
        {
            ["domains"] = new JsonArray(settings.ExamModeDomains.Select(value => (JsonNode)value).ToArray()),
            ["apps"] = new JsonArray(settings.ExamModeApps.Select(value => (JsonNode)value).ToArray()),
        });
        var devices = ExamDeviceControls.Describe(settings.ExamModeMuteAudio, settings.ExamModeDisableCamera, settings.ExamModeBlockUsbStorage);
        try
        {
            await session.CallAsync(Cmd.ExamDevicesSet, new JsonObject
            {
                [ExamDeviceControls.MuteAudio] = settings.ExamModeMuteAudio,
                [ExamDeviceControls.DisableCamera] = settings.ExamModeDisableCamera,
                [ExamDeviceControls.BlockUsbStorage] = settings.ExamModeBlockUsbStorage,
            });
        }
        catch (UnsupportedFeatureException) when (devices.Length == 0)
        {
            // An older Client has no device controls, and none were asked for.
        }
        catch (Exception ex) when (ex is UnsupportedFeatureException or RemoteException)
        {
            throw new InvalidOperationException($"Websites and apps restricted, but the device controls ({devices}) were not applied: {ex.Message}");
        }
        return devices.Length == 0 ? "Exam mode on" : $"Exam mode on ({devices})";
    }
}
