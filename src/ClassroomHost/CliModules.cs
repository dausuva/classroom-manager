// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Modules;

namespace ClassroomManager.Host;

/// <summary>What <c>ClassroomCli modules</c> prints: every module of this Host, whether it is on, what it asks for, and why a package was refused.</summary>
public static class CliModules
{
    public static List<CliResult> List(ModuleRegistry registry, HostSettings settings) =>
        registry.All.Select(module => new CliResult(module.Name, module.Id, module.Source == ModuleSource.BuiltIn ? "built in" : "package", module.Usable, Describe(module, settings))).ToList();

    /// <summary>Stages an install, a removal or a rollback and says what will happen. Exit code 0 when it is staged, 1 when it was refused.</summary>
    public static int Change(string action, string name, System.IO.TextWriter output, bool json)
    {
        var root = BuiltInModules.ModulesFolder;
        var verifier = new WinTrustSignatureVerifier();
        var change = action switch
        {
            "install" => ModulePackageStore.Stage(name, root, verifier, BuiltInModules.TrustedSigners),
            "remove" => ModulePackageStore.StageRemoval(name, root),
            _ => ModulePackageStore.StageRollback(name, root),
        };
        var results = new List<CliResult> { new(change.Id.Length > 0 ? change.Id : name, change.Id, "", change.Done, string.Join(" ", change.Messages)) };
        if (json) output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { results }));
        else foreach (var result in results) output.WriteLine($"{(result.Ok ? "staged " : "refused")}  {result.Computer}  {result.Detail}");
        return change.Done ? 0 : 1;
    }

    public static string Describe(ModuleRegistration module, HostSettings settings)
    {
        if (!module.Usable) return $"not loaded - {string.Join(" ", module.Problems)}";
        var ports = module.Descriptor!.Ports.Select(port => port.Name).ToList();
        var state = BuiltInModules.Enabled(settings, module.Id) ? "on " : "off";
        return $"{state}  {module.Id}  ports: {(ports.Count == 0 ? "none" : string.Join(", ", ports))}" +
               (module.Descriptor.SystemCommands.Count > 0 ? "  (administrator rights: " + string.Join(", ", module.Descriptor.SystemCommands) + ")" : "");
    }
}
