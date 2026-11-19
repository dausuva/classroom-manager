// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// A packaged Client (docs/windows/CLIENT-MSIX.md) has no setup script, so its first start builds client.json from the kit file the administrator left in the
/// data folder, protects the folder, opens the three firewall rules and removes the kit. Everything here is for the administrator and the Client log, in English,
/// and is listed in StringExtractor.NotShown; the two refusals the Host shows are in <see cref="PackagedClient"/>.
/// </summary>
static class PackagedKit
{
    public const string KitFileName = "classroom-kit.json";

    // ---- the kit file -> client.json -------------------------------------------------

    public sealed record KitResult(string? ConfigJson, string? Error);

    /// <summary>The client.json text for a kit file (the same rules as the setup scripts: key length, named keys, roles), or the reason it cannot be used. A key never appears in the reason.</summary>
    public static KitResult BuildConfig(string kitJson)
    {
        JsonObject kit;
        try { kit = JsonNode.Parse(kitJson) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return new(null, "The kit file is not a JSON object."); }

        if (kit["key"] is not JsonValue keyValue || !keyValue.TryGetValue<string>(out var key)) return new(null, "The kit file has no \"key\".");
        if (KeyProblem(key) is { } keyProblem) return new(null, "The shared key: " + keyProblem);

        if (!TryPort(kit["tcpPort"], Protocol.DefaultTcpPort, out var tcp) || !TryPort(kit["udpPort"], Protocol.DefaultUdpPort, out var udp))
            return new(null, "A port in the kit file must be a number from 1 to 65535.");

        var roots = new JsonArray();
        if (kit["allowedRoots"] is JsonArray listed)
        {
            foreach (var item in listed)
            {
                if (item is not JsonValue rootValue || !rootValue.TryGetValue<string>(out var root) || string.IsNullOrWhiteSpace(root)) return new(null, "\"allowedRoots\" must be a list of folders.");
                roots.Add(root);
            }
        }
        if (roots.Count == 0) roots.Add(@"C:\Users");

        var named = new JsonArray();
        if (kit["accessKeys"] is JsonArray accessKeys)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(StringComparer.Ordinal) { key };
            foreach (var entry in accessKeys)
            {
                var name = (entry?["name"] as JsonValue)?.TryGetValue<string>(out var n) == true ? n : "";
                var role = (entry?["role"] as JsonValue)?.TryGetValue<string>(out var r) == true ? r : "";
                var psk = (entry?["key"] as JsonValue)?.TryGetValue<string>(out var k) == true ? k : "";
                if (string.IsNullOrWhiteSpace(name)) return new(null, "An access key has no name.");
                if (name != name.Trim() || name.Length > 40 || name.Any(char.IsControl)) return new(null, $"The access key name '{name}' must be at most 40 characters, without spaces at either end or control characters.");
                if (name.Equals("Lab key", StringComparison.OrdinalIgnoreCase)) return new(null, "'Lab key' is reserved for the lab's original shared key.");
                if (!role.Equals("Teacher", StringComparison.OrdinalIgnoreCase) && !role.Equals("Admin", StringComparison.OrdinalIgnoreCase)) return new(null, $"Access key '{name}': the role must be Teacher or Admin.");
                if (psk.StartsWith("replace-with-", StringComparison.Ordinal)) return new(null, $"Access key '{name}' still has the placeholder key from the example file.");
                if (KeyProblem(psk) is { } problem) return new(null, $"Access key '{name}': " + problem);
                if (!names.Add(name)) return new(null, $"Two access keys are named '{name}'.");
                if (!keys.Add(psk)) return new(null, $"Access key '{name}' uses a key that another key (or the lab key) already uses; every key must be different.");
                named.Add(new JsonObject { ["name"] = name, ["role"] = role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "Teacher", ["psk"] = psk });
            }
        }

        var config = new JsonObject { ["psk"] = key, ["tcpPort"] = tcp, ["udpPort"] = udp, ["allowedRoots"] = roots };
        if (named.Count > 0) config["accessKeys"] = named;
        return new(config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), null);
    }

    static string? KeyProblem(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "is empty.";
        if (key != key.Trim()) return "must not start or end with spaces.";
        if (key.Length < 12) return "must be at least 12 characters.";
        if (key.Length > 256) return "must be at most 256 characters.";
        if (key.Any(char.IsControl)) return "must not contain control characters.";
        return null;
    }

    static bool TryPort(JsonNode? node, int fallback, out int port)
    {
        port = fallback;
        if (node is null) return true;
        return node is JsonValue value && value.TryGetValue<int>(out port) && port is >= 1 and <= 65535;
    }

    // ---- first start ------------------------------------------------------------------

    /// <summary>
    /// A packaged Client with no client.json and a kit file in the data folder: protect the folder, write client.json, open the firewall, remove the kit.
    /// Returns true when a configuration was written. Everything is logged; a key never is. The folder protection and the firewall are replaceable so that tests change neither.
    /// </summary>
    public static bool BootstrapFromKit(bool packaged, string dataDir, Action<string> log, Action<IReadOnlyList<string[]>>? applyFirewall = null, Action<string>? protectFolder = null)
    {
        if (!packaged) return false;
        var configPath = Path.Combine(dataDir, "client.json");
        var kitPath = Path.Combine(dataDir, KitFileName);
        if (File.Exists(configPath)) return false;
        if (!File.Exists(kitPath)) { log($"Packaged Client: no {KitFileName} in {dataDir} yet; waiting for the administrator to put one there."); return false; }

        KitResult result;
        try { result = BuildConfig(File.ReadAllText(kitPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log("Packaged Client: the kit file could not be read: " + ex.Message); return false; }
        if (result.ConfigJson is null) { log("Packaged Client: the kit file was not used. " + result.Error); return false; }

        try
        {
            (protectFolder ?? ProtectDataFolder)(dataDir);
            var temporary = configPath + ".new";
            File.WriteAllText(temporary, result.ConfigJson);
            File.Move(temporary, configPath);
            var config = ClientConfig.Parse(result.ConfigJson)!;
            (applyFirewall ?? ApplyFirewall)(FirewallCommands(config.TcpPort, config.UdpPort));
            File.Delete(kitPath);   // the key now lives only in client.json, which only SYSTEM and administrators can read
            log("Packaged Client: client.json written from the kit file; the kit file was removed.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log("Packaged Client: the configuration could not be written: " + ex.Message);
            return false;
        }
    }

    /// <summary>The data folder as the setup scripts leave it: SYSTEM and administrators have full control, users may only list it and read Staging.</summary>
    static void ProtectDataFolder(string dataDir)
    {
        var staging = Path.Combine(dataDir, "Staging");
        Directory.CreateDirectory(staging);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var folder = new DirectoryInfo(dataDir);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(security);
        var stagingSecurity = new DirectoryInfo(staging).GetAccessControl();
        stagingSecurity.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, both, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(staging).SetAccessControl(stagingSecurity);
    }

    // ---- firewall ---------------------------------------------------------------------

    /// <summary>The netsh arguments for the three inbound rules the setup scripts create (Domain and Private networks only): TCP, UDP, and ping.</summary>
    public static IReadOnlyList<string[]> FirewallCommands(int tcpPort, int udpPort)
    {
        var commands = new List<string[]>();
        foreach (var name in new[] { "Classroom Manager TCP", "Classroom Manager UDP", "Classroom Manager ICMP" })
            commands.Add(["advfirewall", "firewall", "delete", "rule", $"name={name}"]);
        commands.Add(["advfirewall", "firewall", "add", "rule", "name=Classroom Manager TCP", "dir=in", "action=allow", "protocol=TCP", $"localport={tcpPort}", "profile=domain,private"]);
        commands.Add(["advfirewall", "firewall", "add", "rule", "name=Classroom Manager UDP", "dir=in", "action=allow", "protocol=UDP", $"localport={udpPort}", "profile=domain,private"]);
        commands.Add(["advfirewall", "firewall", "add", "rule", "name=Classroom Manager ICMP", "dir=in", "action=allow", "protocol=icmpv4:8,any", "profile=domain,private"]);
        return commands;
    }

    static void ApplyFirewall(IReadOnlyList<string[]> commands)
    {
        foreach (var arguments in commands)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("netsh could not be started.");
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            // Deleting a rule that is not there exits non-zero; only a failed add is a problem.
            if (arguments[2] == "add" && process.ExitCode != 0) throw new InvalidOperationException($"netsh could not add the rule '{arguments[4]}'.");
        }
    }
}
