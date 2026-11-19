// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Lists and uninstalls programs from the registry "Uninstall" keys.
/// Service (elevated): machine-wide programs (HKLM, 64- and 32-bit views).
/// User helper (standard): the signed-in user's own programs (HKCU).
/// The host only sends a registry key name; the command line is built here from the registry.
/// </summary>
sealed class ProgramsHandler
{
    const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    static readonly Regex GuidRx = new(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", RegexOptions.Compiled);

    static RegistryKey Hive(RegistryView view) =>
        RegistryKey.OpenBaseKey(AgentContext.IsUserAgent ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, view);

    public Response List()
    {
        var items = new List<JsonObject>();
        foreach (var view in RegistryViews.All)
        {
            using var root = Hive(view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall is null) continue;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var k = uninstall.OpenSubKey(name);
                if (k is null) continue;

                var display = k.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(display)) continue;
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (!string.IsNullOrEmpty(k.GetValue("ParentKeyName") as string)) continue; // updates / patches
                if (string.IsNullOrWhiteSpace(k.GetValue("UninstallString") as string) &&
                    string.IsNullOrWhiteSpace(k.GetValue("QuietUninstallString") as string)) continue;

                bool msi = IsMsi(k, name);
                var uninstallString = k.GetValue("UninstallString") as string ?? "";
                var quietUninstallString = k.GetValue("QuietUninstallString") as string ?? "";
                var guidance = UninstallGuidance.Analyze(msi, quietUninstallString, uninstallString);
                items.Add(new JsonObject
                {
                    ["key"] = name,
                    ["view"] = view == RegistryView.Registry64 ? "64" : "32",
                    ["name"] = display,
                    ["version"] = k.GetValue("DisplayVersion") as string ?? "",
                    ["publisher"] = k.GetValue("Publisher") as string ?? "",
                    ["silent"] = guidance.IsSilent,
                    ["silentSource"] = guidance.Source,
                    ["suggestedSilentArgs"] = guidance.SuggestedArgs,
                    ["silentNote"] = guidance.Note,
                    ["uninstallString"] = Environment.ExpandEnvironmentVariables(uninstallString),
                    ["quietUninstallString"] = Environment.ExpandEnvironmentVariables(quietUninstallString),
                    // Used to correlate an autorun entry's executable with the program that installed it (see AutorunHandler).
                    ["installLocation"] = k.GetValue("InstallLocation") as string ?? "",
                    ["displayIcon"] = Environment.ExpandEnvironmentVariables(k.GetValue("DisplayIcon") as string ?? ""),
                });
            }
        }

        var arr = new JsonArray();
        foreach (var it in items.OrderBy(i => i["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase))
            arr.Add(it);
        return Response.Success(new JsonObject
        {
            ["machineName"] = Environment.MachineName,
            ["scope"] = AgentContext.IsUserAgent ? "user" : "machine",
            ["programs"] = arr,
        });
    }

    static bool IsMsi(RegistryKey k, string keyName)
    {
        if (k.GetValue("WindowsInstaller") is int wi && wi == 1) return true;
        var us = k.GetValue("UninstallString") as string ?? "";
        return us.Contains("msiexec", StringComparison.OrdinalIgnoreCase) && (GuidRx.IsMatch(keyName) || GuidRx.IsMatch(us));
    }

    public async Task<Response> UninstallAsync(JsonObject a, CancellationToken ct)
    {
        var keyName = a.Str("key");
        var view = a.Str("view", "64") == "32" ? RegistryView.Registry32 : RegistryView.Registry64;
        var extraArgs = a.Str("args", "");
        int timeoutSec = a.Int("timeoutSec") ?? 1800;

        using var root = Hive(view);
        using var k = root.OpenSubKey($@"{UninstallPath}\{keyName}");
        if (k is null) return Response.Fail("Program not found in the registry (it may already be removed).");

        var display = k.GetValue("DisplayName") as string ?? keyName;
        var uninstallString = k.GetValue("UninstallString") as string ?? "";
        var quiet = k.GetValue("QuietUninstallString") as string ?? "";

        ProcessStartInfo psi;
        if (IsMsi(k, keyName))
        {
            var m = GuidRx.Match(keyName);
            if (!m.Success) m = GuidRx.Match(uninstallString);
            if (!m.Success) return Response.Fail("Could not determine the MSI product code.");
            psi = new ProcessStartInfo("msiexec.exe", $"/x {m.Value} /qn /norestart");
        }
        else
        {
            string cmdline;
            if (!string.IsNullOrWhiteSpace(quiet)) cmdline = quiet;
            else if (!string.IsNullOrWhiteSpace(extraArgs)) cmdline = $"{uninstallString} {extraArgs}";
            else
                return Response.Fail($"'{display}' has no silent uninstall command. " +
                                     "Supply silent arguments documented by the application's publisher.");

            // cmd strips the outer quotes, so paths with spaces inside cmdline keep working.
            psi = new ProcessStartInfo("cmd.exe", $"/c \"{cmdline}\"");
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        var result = await InstallHandler.RunAsync(psi, timeoutSec, ct);
        Log.Write($"Uninstall '{display}' -> {(result.Ok ? "ok" : result.Error)}");
        return result;
    }
}
