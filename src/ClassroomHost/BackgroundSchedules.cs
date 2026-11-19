// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.IO;

namespace ClassroomManager.Host;

/// <summary>
/// "Run schedules when the Host is closed": one Windows Task Scheduler task for this Windows user that runs
/// <c>ClassroomHost.exe --cli schedule run-due --yes</c> every minute. It runs as the user, while the user is signed in, so it reads
/// the same settings and keys as the Host and needs no stored password. ClassroomHost.exe is a windowed program, so nothing flashes on
/// the screen; the process lives a few seconds and does nothing when no schedule is due.
/// </summary>
public static class BackgroundSchedules
{
    /// <summary>The task's name: one per Windows user, in a Classroom Manager folder of Task Scheduler.</summary>
    public static string TaskName(string userName) => $@"\Classroom Manager\Host schedules ({Sanitize(userName)})";

    /// <summary>What the task runs, after the program: run the schedules due now, the risky ones included, which the teacher enabled.</summary>
    public const string Arguments = "--cli schedule run-due --yes";

    /// <summary>The program the task starts: the windowed Host (never the console ClassroomCli.exe, which would flash a window each minute).</summary>
    public static string HostProgram(string processPath)
    {
        var folder = Path.GetDirectoryName(processPath) ?? "";
        var host = Path.Combine(folder, "ClassroomHost.exe");
        return File.Exists(host) ? host : processPath;
    }

    public static List<string> CreateArguments(string userName, string program) =>
    [
        "/Create", "/TN", TaskName(userName), "/TR", $"\"{program}\" {Arguments}",
        // Every minute, only while this user is signed in, so no password is stored; /F replaces an earlier task (a moved program).
        "/SC", "MINUTE", "/MO", "1", "/IT", "/F",
    ];

    public static List<string> DeleteArguments(string userName) => ["/Delete", "/TN", TaskName(userName), "/F"];

    public static List<string> QueryArguments(string userName) => ["/Query", "/TN", TaskName(userName), "/FO", "LIST"];

    /// <summary>Turns the task on (creates or refreshes it) or off. Returns null on success, or what went wrong.</summary>
    public static string? Apply(bool on, string userName, string processPath)
    {
        if (!on && !Exists(userName)) return null;
        var (exit, output) = Run(on ? CreateArguments(userName, HostProgram(processPath)) : DeleteArguments(userName));
        return exit == 0 ? null : $"Windows Task Scheduler refused ({exit}): {output.Trim()}";
    }

    /// <summary>True when this user's task exists.</summary>
    public static bool Exists(string userName) => Run(QueryArguments(userName)).ExitCode == 0;

    static (int ExitCode, string Output) Run(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000)) { process.Kill(); return (-1, "schtasks did not answer in 30 seconds."); }
            return (process.ExitCode, output.Result + error);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, ex.Message); }
    }

    /// <summary>A user name as Task Scheduler accepts it in a task name (no backslashes or other path characters).</summary>
    static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = string.Concat(name.Select(ch => bad.Contains(ch) ? '_' : ch)).Trim();
        return clean.Length == 0 ? "user" : clean;
    }
}
