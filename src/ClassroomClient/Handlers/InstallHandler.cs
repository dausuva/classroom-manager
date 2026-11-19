// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

sealed class InstallHandler
{
    readonly Func<ProcessStartInfo, int, CancellationToken, Task<Response>> _run;
    public InstallHandler(Func<ProcessStartInfo, int, CancellationToken, Task<Response>>? run = null) => _run = run ?? RunAsync;
    static readonly Regex WingetId = new(@"^[A-Za-z0-9][A-Za-z0-9._+\-]*$", RegexOptions.Compiled);

    // 0 = success, 3010 = success (reboot required), 1641 = success (reboot initiated)
    static bool IsSuccess(int code) => code is 0 or 3010 or 1641;

    /// <summary>Runs an installer already uploaded by the host (.exe / .msi).</summary>
    public async Task<Response> RunInstallerAsync(JsonObject a, CancellationToken ct)
    {
        var path = a.Str("path");
        var args = a.Str("args", "");
        int timeoutSec = a.Int("timeoutSec") ?? 1800;

        if (!File.Exists(path)) return Response.Fail("Installer file was not found.");
        var operation = Guid.NewGuid().ToString("N");

        try
        {
            MemoryReclaimer.ScheduleAfterLargeTransfer(new FileInfo(path).Length);
            ProcessStartInfo psi;
            if (Path.GetExtension(path).Equals(".msi", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(args)) args = "/qn /norestart";
                var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
                psi = new ProcessStartInfo(msiexec, $"/i \"{path}\" {args}");
            }
            else
            {
                psi = new ProcessStartInfo(path, args);
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.SystemDirectory;
            // Installer paths and arguments may contain passwords, tokens or private share names.
            // Keep correlation and exit metadata, never the request or exception's raw text.
            Log.Write($"install.run [{operation}]: starting; arguments omitted.");
            var result = await _run(psi, timeoutSec, ct);
            var exitCode = result.Data["exitCode"] is JsonValue value && value.TryGetValue<int>(out var code)
                ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable";
            Log.Write($"install.run [{operation}]: completed; ok={result.Ok}; exitCode={exitCode}.");
            return result;
        }
        catch (Exception ex)
        {
            Log.Write($"install.run [{operation}]: could not start ({ex.GetType().Name}).");
            return Response.Fail($"Could not start installer ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// winget install / uninstall by package id, e.g. "VideoLAN.VLC".
    /// args: id, action ("install" | "uninstall", default install), scope ("user" | "machine" | ""), timeoutSec.
    /// </summary>
    public async Task<Response> WingetAsync(JsonObject a, CancellationToken ct)
    {
        var id = a.Str("id");
        if (!WingetId.IsMatch(id)) return Response.Fail("Invalid winget package id.");
        var action = a.Str("action", "install").Equals("uninstall", StringComparison.OrdinalIgnoreCase) ? "uninstall" : "install";
        var scope = a.Str("scope", "").ToLowerInvariant();
        int timeoutSec = a.Int("timeoutSec") ?? 1800;

        string arguments = action == "uninstall"
            ? $"uninstall --id {id} -e --silent --disable-interactivity --accept-source-agreements"
            : $"install --id {id} -e --silent --disable-interactivity --accept-package-agreements --accept-source-agreements";
        if (scope is "user" or "machine") arguments += $" --scope {scope}";

        var psi = new ProcessStartInfo(FindWinget(), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var operation = Guid.NewGuid().ToString("N");
        Log.Write($"install.winget [{operation}]: starting; action={action}; package details omitted.");
        var result = await _run(psi, timeoutSec, ct);
        var exitCode = result.Data["exitCode"] is JsonValue value && value.TryGetValue<int>(out var code)
            ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable";
        Log.Write($"install.winget [{operation}]: completed; ok={result.Ok}; exitCode={exitCode}.");
        return result;
    }

    /// <summary>
    /// Finds winget. Under SYSTEM there is no per-user "App execution alias", so look inside WindowsApps too.
    /// (winget under SYSTEM is officially limited: prefer installer files / MSI for machine-wide installs.)
    /// </summary>
    static string FindWinget()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        try
        {
            var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            foreach (var d in Directory.GetDirectories(apps, "Microsoft.DesktopAppInstaller_*_8wekyb3d8bbwe")
                         .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(d, "winget.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }

        return "winget.exe";
    }

    /// <summary>Starts a process, waits for it (with timeout) and maps its exit code to a response.</summary>
    internal static async Task<Response> RunAsync(ProcessStartInfo psi, int timeoutSec, CancellationToken ct)
    {
        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { return Response.Fail($"Could not start process ({ex.GetType().Name})."); }
        if (proc is null) return Response.Fail("Could not start process.");

        using (proc)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return Response.Fail($"Timed out after {timeoutSec}s and was stopped. " +
                    "(If it was waiting for a window or an admin prompt, use silent switches or the other level.)");
            }

            int code = proc.ExitCode;
            var data = new JsonObject { ["exitCode"] = code, ["rebootRequired"] = code is 3010 or 1641 };
            return IsSuccess(code)
                ? Response.Success(data)
                : new Response { Ok = false, Error = $"Exited with code {code}.", Data = data };
        }
    }
}
