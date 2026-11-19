// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Reads and edits the registry, and pushes/pulls whole ".reg" patch files via reg.exe.
/// Service (elevated): any hive. User helper (standard): restricted to HKEY_CURRENT_USER,
/// regardless of what the request asks for, since that's the only hive it has rights to.
/// </summary>
sealed class RegistryHandler
{
    readonly Func<ProcessStartInfo, int, CancellationToken, Task<Response>> _run;
    readonly string _stagingRoot;
    public RegistryHandler(Func<ProcessStartInfo, int, CancellationToken, Task<Response>>? run = null, string? stagingRoot = null)
    {
        _run = run ?? InstallHandler.RunAsync;
        _stagingRoot = stagingRoot ?? ClientConfig.StagingDir;
    }
    /// <summary>Refuses to touch these - editing them by hand from a remote patch is a bricking risk
    /// far out of proportion to anything a classroom tool should be doing.</summary>
    static readonly string[] ProtectedPrefixes =
    {
        @"HKLM\SYSTEM", @"HKLM\SECURITY", @"HKLM\SAM",
    };

    public Response List(JsonObject a)
    {
        try
        {
            using var key = Open(a, writable: false);
            if (key is null) return Response.Fail("Registry key not found.");

            var subkeys = new JsonArray();
            foreach (var name in key.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                subkeys.Add(name);

            var values = new JsonArray();
            foreach (var name in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var kind = key.GetValueKind(name);
                values.Add(new JsonObject
                {
                    ["name"] = name.Length == 0 ? "(Default)" : name,
                    ["type"] = kind.ToString(),
                    ["data"] = FormatValue(key.GetValue(name), kind),
                });
            }

            return Response.Success(new JsonObject
            {
                ["hive"] = a.Str("hive"),
                ["path"] = a.Str("path", ""),
                ["subkeys"] = subkeys,
                ["values"] = values,
            });
        }
        catch (UnauthorizedAccessException ex) { return Response.Fail(ex.Message); }
        catch (System.Security.SecurityException) { return Response.Fail("Access denied to that registry key."); }
    }

    public Response SetValue(JsonObject a)
    {
        var refuse = RefuseIfProtected(a);
        if (refuse is not null) return refuse;

        try
        {
            using var key = Open(a, writable: true, create: true);
            if (key is null) return Response.Fail("Registry key not found.");

            var name = a.Str("name", "");
            if (name == "(Default)") name = "";
            var kind = Enum.TryParse<RegistryValueKind>(a.Str("type", "String"), true, out var k) ? k : RegistryValueKind.String;
            var raw = a.Str("data", "0").Trim();
            var hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
            object value = kind switch
            {
                RegistryValueKind.DWord => Convert.ToInt32(hex, raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10),
                RegistryValueKind.QWord => Convert.ToInt64(hex, raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10),
                RegistryValueKind.MultiString => a.Str("data", "").Split('\n').Select(s => s.TrimEnd('\r')).ToArray(),
                RegistryValueKind.Binary => Convert.FromHexString(a.Str("data", "").Replace(" ", "").Replace("-", "")),
                _ => a.Str("data", ""),
            };
            key.SetValue(name, value, kind);
            return Response.Success();
        }
        catch (Exception ex) { return Response.Fail($"Could not set the value: {ex.Message}"); }
    }

    public Response DeleteValue(JsonObject a)
    {
        var refuse = RefuseIfProtected(a);
        if (refuse is not null) return refuse;

        try
        {
            using var key = Open(a, writable: true);
            if (key is null) return Response.Fail("Registry key not found.");
            var name = a.Str("name", "");
            if (name == "(Default)") name = "";
            key.DeleteValue(name, throwOnMissingValue: false);
            return Response.Success();
        }
        catch (Exception ex) { return Response.Fail($"Could not delete the value: {ex.Message}"); }
    }

    public Response DeleteKey(JsonObject a)
    {
        var refuse = RefuseIfProtected(a);
        if (refuse is not null) return refuse;

        var path = a.Str("path", "").Trim('\\');
        if (path.Length == 0) return Response.Fail("Refusing to delete a hive root.");

        try
        {
            using var parent = OpenHive(a);
            if (parent is null) return Response.Fail("Access denied to that hive.");
            var parentPath = path.Contains('\\') ? path[..path.LastIndexOf('\\')] : "";
            var name = path.Contains('\\') ? path[(path.LastIndexOf('\\') + 1)..] : path;
            using var parentKey = parentPath.Length == 0 ? parent : parent.OpenSubKey(parentPath, writable: true);
            if (parentKey is null) return Response.Fail("Registry key not found.");
            parentKey.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
            return Response.Success();
        }
        catch (Exception ex) { return Response.Fail($"Could not delete the key: {ex.Message}"); }
    }

    /// <summary>Exports a key to a .reg file in the staging folder via reg.exe, so the host can download it
    /// the same way it downloads any other staged file (see FileHandler - the staging folder is always readable).</summary>
    public async Task<Response> ExportAsync(JsonObject a, CancellationToken ct)
    {
        var (hiveArg, fullPath) = HiveAndPath(a);
        if (hiveArg is null) return Response.Fail("Unknown or disallowed hive.");

        Directory.CreateDirectory(ClientConfig.StagingDir);
        var fileName = a.Str("fileName", "export.reg");
        if (fileName.Contains("..") || Path.IsPathRooted(fileName)) fileName = "export.reg";
        var dest = Path.Combine(ClientConfig.StagingDir, fileName);

        var psi = new ProcessStartInfo("reg.exe", $"export \"{fullPath}\" \"{dest}\" /y")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var result = await InstallHandler.RunAsync(psi, 60, ct);
        if (!result.Ok) return result;

        return Response.Success(new JsonObject { ["path"] = dest });
    }

    /// <summary>Imports a .reg file the host has already uploaded (via fs.write) into the staging folder.
    /// This is the "push registry files as programs" path - same shape as install.run.</summary>
    public async Task<Response> ImportAsync(JsonObject a, CancellationToken ct)
    {
        var path = a.Str("path");
        var full = Path.GetFullPath(path);
        var stagingFull = Path.GetFullPath(_stagingRoot);
        if (!full.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
            return Response.Fail("Registry patches may only be imported from the staging folder.");
        if (!File.Exists(full)) return Response.Fail($".reg file not found: {path}");

        var view = a.Str("view", "default").ToLowerInvariant();
        if (view is not ("default" or "32" or "64"))
            return Response.Fail("Registry view must be default, 32, or 64.");
        var viewArgument = view == "default" ? "" : $" /reg:{view}";
        var psi = new ProcessStartInfo("reg.exe", $"import \"{full}\"{viewArgument}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var result = await _run(psi, 120, ct);
        var code = result.Data["exitCode"] is JsonValue exit && exit.TryGetValue<int>(out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable";
        Log.Write($"registry.import: completed; ok={result.Ok}; exitCode={code}.");
        return result;
    }

    // --- helpers ---------------------------------------------------------

    static string FormatValue(object? value, RegistryValueKind kind) => value switch
    {
        null => "",
        string[] arr => string.Join("\n", arr),
        byte[] bytes => Convert.ToHexString(bytes),
        _ => value.ToString() ?? "",
    };

    Response? RefuseIfProtected(JsonObject a)
    {
        var (hiveArg, _) = HiveAndPath(a);
        if (hiveArg is not null && ProtectedPrefixes.Any(p => hiveArg.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return Response.Fail("Refusing to edit this part of the registry (SYSTEM/SECURITY/SAM) - " +
                                  "the risk of leaving Windows unbootable isn't worth it for a classroom tool.");
        return null;
    }

    /// <summary>Builds the "HKLM\Some\Path" style argument reg.exe expects, and validates the hive.
    /// In the user helper, only HKCU is allowed no matter what the request asks for.</summary>
    (string? hiveArg, string fullPath) HiveAndPath(JsonObject a)
    {
        var hive = a.Str("hive", "HKLM").ToUpperInvariant();
        var path = a.Str("path", "").Trim('\\');
        if (AgentContext.IsUserAgent) hive = "HKCU";
        if (hive is not ("HKLM" or "HKCU" or "HKCR" or "HKU" or "HKCC")) return (null, "");
        var full = path.Length == 0 ? hive : $@"{hive}\{path}";
        return (hive, full);
    }

    RegistryKey? OpenHive(JsonObject a)
    {
        var hive = a.Str("hive", "HKLM").ToUpperInvariant();
        if (AgentContext.IsUserAgent) hive = "HKCU";
        var view = a.Str("view", "64") == "32" ? RegistryView.Registry32 : RegistryView.Registry64;
        return hive switch
        {
            "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view),
            "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view),
            "HKCR" => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view),
            "HKU" => RegistryKey.OpenBaseKey(RegistryHive.Users, view),
            "HKCC" => RegistryKey.OpenBaseKey(RegistryHive.CurrentConfig, view),
            _ => null,
        };
    }

    RegistryKey? Open(JsonObject a, bool writable, bool create = false)
    {
        using var root = OpenHive(a);
        if (root is null) return null;
        var path = a.Str("path", "").Trim('\\');
        if (path.Length == 0) return RegistryKey.OpenBaseKey(RootHiveOf(root), RootViewOf(a));

        if (create) return root.CreateSubKey(path, writable);
        return root.OpenSubKey(path, writable);
    }

    static RegistryHive RootHiveOf(RegistryKey root) => root.Name switch
    {
        "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
        "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
        "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
        "HKEY_USERS" => RegistryHive.Users,
        "HKEY_CURRENT_CONFIG" => RegistryHive.CurrentConfig,
        _ => RegistryHive.LocalMachine,
    };

    static RegistryView RootViewOf(JsonObject a) => a.Str("view", "64") == "32" ? RegistryView.Registry32 : RegistryView.Registry64;
}
