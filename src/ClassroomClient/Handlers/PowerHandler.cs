// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Shut down, restart, sleep, log off or cancel a pending shutdown.</summary>
sealed class PowerHandler
{
    [DllImport("powrprof.dll", SetLastError = true)]
    static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public async Task<Response> RunAsync(JsonObject a, CancellationToken ct)
    {
        var action = a.Str("action").ToLowerInvariant();
        int delay = Math.Clamp(a.Int("delaySec") ?? 30, 0, 3600);
        var message = a.Str("message", Loc.T("This computer will shut down. Please save your work."));
        message = message.Replace("\"", "'");
        if (message.Length > 200) message = message[..200];

        if (action == "sleep")
        {
            _ = SleepAfterResponseAsync();
            return Response.Success(new JsonObject { ["note"] = "Sleep scheduled." });
        }

        string args = action switch
        {
            "shutdown" => $"/s /f /t {delay} /c \"{message}\"",
            "restart" => $"/r /f /t {delay} /c \"{message}\"",
            "logoff" => "/l",          // runs in the user helper, so it logs off that user
            "cancel" => "/a",
            _ => "",
        };
        if (args.Length == 0) return Response.Fail($"Unknown power action '{action}'.");

        var psi = new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true };
        var result = await InstallHandler.RunAsync(psi, 30, ct);
        // shutdown /a exits with 1116 (ERROR_NO_SHUTDOWN_IN_PROGRESS) when there is nothing to
        // cancel - that's the outcome the teacher wanted, not an error.
        if (action == "cancel" && !result.Ok && (result.Error ?? "").Contains("1116"))
            result = Response.Success(new JsonObject { ["note"] = "No shutdown or restart was pending." });
        Log.Write($"power.action {action} -> {(result.Ok ? "ok" : result.Error)}");
        return result;
    }

    static async Task SleepAfterResponseAsync()
    {
        // Let CommandRouter send the success response before the network adapter is suspended.
        await Task.Delay(1000);
        try
        {
            if (SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false))
                Log.Write("power.action sleep -> ok");
            else
                Log.Write($"power.action sleep -> Windows error {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { Log.Write("power.action sleep -> " + ex.Message); }
    }
}
