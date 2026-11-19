// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Pulls together the three common ways a program launches itself automatically - registry Run/RunOnce
/// keys, Startup-folder shortcuts, and Scheduled Tasks - into one list, and tags each entry with the
/// installed program it appears to belong to (see ProgramsHandler.InstallLocation), so the host can offer
/// "uninstall the program", "block the process" or "just remove this autorun entry" per row.
///
/// Service (elevated): HKLM Run/RunOnce (both registry views), the machine Startup folder, and Scheduled
/// Tasks. User helper (standard): HKCU Run/RunOnce and the user's own Startup folder only - it has no
/// rights to see other users' entries or the task scheduler's machine-wide list.
/// </summary>
sealed class AutorunHandler
{
    const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string RunOncePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";

    readonly TasksHandler _tasks = new();

    public async Task<Response> ListAsync(CancellationToken ct)
    {
        var entries = new List<JsonObject>();

        if (AgentContext.IsUserAgent)
        {
            AddRegistryRun(entries, RegistryHive.CurrentUser, RegistryView.Registry64, "user");
            AddStartupFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "user");
        }
        else
        {
            foreach (var view in RegistryViews.All)
                AddRegistryRun(entries, RegistryHive.LocalMachine, view, "machine");
            AddStartupFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "machine");

            var taskResult = await _tasks.ListAsync(ct);
            if (taskResult.Ok)
            {
                foreach (var t in taskResult.Data["tasks"]!.AsArray())
                {
                    var cmd = t!["taskToRun"]!.GetValue<string>();
                    entries.Add(new JsonObject
                    {
                        ["kind"] = "task",
                        ["scope"] = "machine",
                        ["source"] = t["name"]!.GetValue<string>(),
                        ["name"] = t["name"]!.GetValue<string>(),
                        ["command"] = cmd,
                        ["enabled"] = !string.Equals(t["status"]!.GetValue<string>(), "Disabled", StringComparison.OrdinalIgnoreCase),
                    });
                }
            }
        }

        // Correlate against installed programs by prefix-matching the resolved executable path.
        var programs = new ProgramsHandler().List();
        var installLocations = programs.Ok
            ? programs.Data["programs"]!.AsArray()
                .Select(p => (name: p!["name"]!.GetValue<string>(), loc: p["installLocation"]!.GetValue<string>()))
                .Where(x => x.loc.Length > 3)
                .ToList()
            : new List<(string name, string loc)>();

        var arr = new JsonArray();
        foreach (var e in entries)
        {
            var exePath = ExtractPath(e["command"]!.GetValue<string>());
            var match = installLocations.FirstOrDefault(p => exePath.StartsWith(p.loc.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (match.name is not null) e["matchedProgram"] = match.name;
            arr.Add(e);
        }

        return Response.Success(new JsonObject { ["entries"] = arr });
    }

    void AddRegistryRun(List<JsonObject> entries, RegistryHive hive, RegistryView view, string scope)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        foreach (var (subPath, runOnce) in new[] { (RunPath, false), (RunOncePath, true) })
        {
            using var key = root.OpenSubKey(subPath);
            if (key is null) continue;
            foreach (var name in key.GetValueNames())
            {
                var cmd = key.GetValue(name) as string ?? "";
                if (cmd.Length == 0) continue;
                entries.Add(new JsonObject
                {
                    ["kind"] = "registry",
                    ["scope"] = scope,
                    ["source"] = $@"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\{subPath} [{(view == RegistryView.Registry64 ? "64" : "32")}-bit]",
                    ["hive"] = hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU",
                    ["view"] = view == RegistryView.Registry64 ? "64" : "32",
                    ["path"] = subPath,
                    ["valueName"] = name,
                    ["name"] = name,
                    ["command"] = cmd,
                    ["runOnce"] = runOnce,
                    ["enabled"] = true,
                });
            }
        }
    }

    void AddStartupFolder(List<JsonObject> entries, string folder, string scope)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file) : file;
            entries.Add(new JsonObject
            {
                ["kind"] = "startup",
                ["scope"] = scope,
                ["source"] = folder,
                ["path"] = file,
                ["name"] = Path.GetFileNameWithoutExtension(file),
                ["command"] = target ?? file,
                ["enabled"] = true,
            });
        }
    }

    /// <summary>Resolves a .lnk shortcut's target via late-bound WScript.Shell COM automation (no extra
    /// native interop / NuGet dependency). Best-effort - a shortcut we can't resolve is still listed,
    /// just without a resolved target to correlate against installed programs.</summary>
    static string? ResolveShortcut(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath;
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }

    /// <summary>Pulls a plausible file path out of a Run-key command line ("C:\Program.exe" /flag) or a resolved shortcut target.</summary>
    static string ExtractPath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0) return command[1..end];
        }
        var space = command.IndexOf(" -");
        if (space < 0) space = command.IndexOf(" /");
        return space > 0 ? command[..space] : command;
    }

    public async Task<Response> RemoveAsync(JsonObject a, CancellationToken ct)
    {
        var kind = a.Str("kind");
        try
        {
            switch (kind)
            {
                case "registry":
                {
                    var hive = a.Str("hive") == "HKCU" || AgentContext.IsUserAgent ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                    var view = a.Str("view", "64") == "32" ? RegistryView.Registry32 : RegistryView.Registry64;
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(a.Str("path"), writable: true);
                    if (key is null) return Response.Fail("Registry key not found (already removed?).");
                    key.DeleteValue(a.Str("valueName"), throwOnMissingValue: false);
                    return Response.Success();
                }
                case "startup":
                {
                    var path = a.Str("path");
                    var startupRoots = new[]
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                    };
                    if (!startupRoots.Any(r => Path.GetFullPath(path).StartsWith(Path.GetFullPath(r), StringComparison.OrdinalIgnoreCase)))
                        return Response.Fail("Refusing to delete a file outside a Startup folder.");
                    if (File.Exists(path)) File.Delete(path);
                    return Response.Success();
                }
                case "task":
                    return await _tasks.DeleteAsync(a, ct);
                default:
                    return Response.Fail($"Unknown autorun kind '{kind}'.");
            }
        }
        catch (Exception ex) { return Response.Fail($"Could not remove the autorun entry: {ex.Message}"); }
    }
}
