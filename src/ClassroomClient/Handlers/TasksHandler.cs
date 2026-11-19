// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Lists and edits Scheduled Tasks by shelling out to schtasks.exe, consistent with the rest of
/// the codebase (msiexec/winget/reg.exe) rather than adding a Task Scheduler COM/NuGet dependency.
/// Machine-wide only - always runs in the elevated service.
/// </summary>
sealed class TasksHandler
{
    public async Task<Response> ListAsync(CancellationToken ct)
    {
        var (ok, output, error) = await RunCaptureAsync("schtasks.exe", "/Query /FO LIST /V", ct);
        if (!ok) return Response.Fail(error);

        var arr = new JsonArray();
        foreach (var block in output.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = new Dictionary<string, string>();
            foreach (var line in block.Split('\n'))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var key = line[..idx].Trim();
                var val = line[(idx + 1)..].Trim();
                fields[key] = val; // later duplicate lines (multiple triggers) overwrite - fine for a summary view
            }
            if (!fields.TryGetValue("TaskName", out var name) || name.Length == 0) continue;
            // Skip the built-in Microsoft housekeeping tasks - noise for a classroom autorun review.
            if (name.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase)) continue;

            arr.Add(new JsonObject
            {
                ["name"] = name,
                ["status"] = fields.GetValueOrDefault("Scheduled Task State", fields.GetValueOrDefault("Status", "")),
                ["nextRun"] = fields.GetValueOrDefault("Next Run Time", ""),
                ["lastRun"] = fields.GetValueOrDefault("Last Run Time", ""),
                ["lastResult"] = fields.GetValueOrDefault("Last Result", ""),
                ["runAs"] = fields.GetValueOrDefault("Run As User", ""),
                ["taskToRun"] = fields.GetValueOrDefault("Task To Run", ""),
                ["author"] = fields.GetValueOrDefault("Author", ""),
            });
        }
        return Response.Success(new JsonObject { ["tasks"] = arr });
    }

    public async Task<Response> SetEnabledAsync(JsonObject a, CancellationToken ct)
    {
        var name = a.Str("name");
        var flag = (a.Bool("enabled") ?? true) ? "/ENABLE" : "/DISABLE";
        var (ok, _, error) = await RunCaptureAsync("schtasks.exe", $"/Change /TN \"{name}\" {flag}", ct);
        return ok ? Response.Success() : Response.Fail(error);
    }

    public async Task<Response> DeleteAsync(JsonObject a, CancellationToken ct)
    {
        var name = a.Str("name");
        var (ok, _, error) = await RunCaptureAsync("schtasks.exe", $"/Delete /TN \"{name}\" /F", ct);
        return ok ? Response.Success() : Response.Fail(error);
    }

    public async Task<Response> RunAsync(JsonObject a, CancellationToken ct)
    {
        var name = a.Str("name");
        var (ok, _, error) = await RunCaptureAsync("schtasks.exe", $"/Run /TN \"{name}\"", ct);
        return ok ? Response.Success() : Response.Fail(error);
    }

    /// <summary>Runs a process and captures stdout, since we need the text (not just an exit code) here.</summary>
    internal static async Task<(bool ok, string output, string error)> RunCaptureAsync(string exe, string args, CancellationToken ct, int timeoutSec = 30)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { return (false, "", $"Could not start {exe}: {ex.Message}"); }
        if (proc is null) return (false, "", $"Could not start {exe}.");

        using (proc)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (false, "", $"Timed out after {timeoutSec}s.");
            }

            if (proc.ExitCode != 0)
                return (false, stdout.ToString(), stderr.ToString().Trim() is { Length: > 0 } e ? e : $"Exited with code {proc.ExitCode}.");
            return (true, stdout.ToString(), "");
        }
    }
}
