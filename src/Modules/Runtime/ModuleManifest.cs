// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClassroomManager.Modules;

/// <summary>The range of module contract versions a module was written for.</summary>
public sealed record ContractRange(int Min, int Max);

/// <summary>Where a module's code starts: an assembly in the package and the type the Host creates.</summary>
public sealed record ModuleEntry(string Assembly, string Type);

/// <summary>What a module needs from the Client on a student PC. The Client's own handlers stay built into the Client.</summary>
public sealed record ClientRequirement(string MinProtocol, IReadOnlyList<string> Capabilities);

/// <summary>One file of the package and the SHA-256 it must have.</summary>
public sealed record ModuleFile(string Path, string Sha256);

/// <summary>
/// The description of an installable module, read from the package's <c>module.json</c>. It is data only:
/// nothing in a manifest is trusted until <see cref="ModulePackage"/> has checked the files and the signature.
/// </summary>
public sealed record ModuleManifest(
    int Schema,
    string Id,
    string Publisher,
    string Name,
    string Version,
    ContractRange Contract,
    string Os,
    string Architecture,
    ModuleEntry Entry,
    ClientRequirement Client,
    IReadOnlyList<ModuleFile> Files,
    IReadOnlyList<ModulePortRequest>? Ports = null,
    IReadOnlyList<string>? Sinks = null,
    IReadOnlyList<string>? SystemCommands = null,
    IReadOnlyList<string>? Uploads = null)
{
    /// <summary>The kinds of upload the module may send; none when the manifest has no <c>uploads</c> section.</summary>
    public IReadOnlyList<string> DeclaredUploads => Uploads ?? [];

    /// <summary>The commands sent with administrator rights; none when the manifest has no <c>systemCommands</c> section.</summary>
    public IReadOnlyList<string> DeclaredSystemCommands => SystemCommands ?? [];

    /// <summary>The sink kinds the module may copy into; none when the manifest has no <c>sinks</c> section.</summary>
    public IReadOnlyList<string> DeclaredSinks => Sinks ?? [];

    /// <summary>The Host services the module asks for; a manifest without a <c>ports</c> section asks for none.</summary>
    public IReadOnlyList<ModulePortRequest> RequestedPorts => Ports ?? [];

    public const int CurrentSchema = 1;
    public const int MaxManifestBytes = 256 * 1024;
    public const int MaxFiles = 64;

    static readonly Regex IdPattern = new(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9-]*){2,}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex CapabilityPattern = new(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9-]*)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex TypePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 8,
    };

    /// <summary>Reads a manifest. Returns null and the reason when the text is not a manifest at all; <see cref="Validate"/> judges the content.</summary>
    public static ModuleManifest? TryParse(string json, out string error)
    {
        error = "";
        try
        {
            var manifest = JsonSerializer.Deserialize<ModuleManifest>(json, Options);
            if (manifest is null) { error = "The manifest is empty."; return null; }
            return manifest;
        }
        catch (JsonException exception) { error = "The manifest is not valid JSON: " + exception.Message; return null; }
    }

    /// <summary>Every rule a manifest breaks, in plain words. An empty list means the manifest is well formed.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Schema != CurrentSchema) problems.Add($"Unsupported manifest schema {Schema}; this Host reads schema {CurrentSchema}.");
        if (Id is null || !IdPattern.IsMatch(Id)) problems.Add("The module id must be lower-case words separated by dots, such as dausuva.reporting.inventory.");
        else if (Publisher is null || !Id.StartsWith(Publisher + ".", StringComparison.Ordinal)) problems.Add("The module id must start with the publisher id and a dot.");
        if (string.IsNullOrWhiteSpace(Publisher)) problems.Add("The publisher is missing.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100) problems.Add("The module name must be 1 to 100 characters.");
        if (!System.Version.TryParse(Version, out var version) || version.Major < 0 || Version.Count(c => c == '.') < 2)
            problems.Add("The module version must have three or four numbers, such as 1.0.0.");
        if (Contract is null || Contract.Min < 1 || Contract.Max < Contract.Min) problems.Add("The contract range must be two numbers with 1 <= min <= max.");
        if (!string.Equals(Os, "windows", StringComparison.Ordinal)) problems.Add("The module must target the operating system \"windows\".");
        if (!string.Equals(Architecture, "x64", StringComparison.Ordinal)) problems.Add("The module must target the architecture \"x64\".");

        if (Files is null || Files.Count == 0) problems.Add("The package lists no files.");
        else
        {
            if (Files.Count > MaxFiles) problems.Add($"The package lists {Files.Count} files; the limit is {MaxFiles}.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Files)
            {
                if (file is null) { problems.Add("A file entry is empty."); continue; }
                var pathProblem = PathProblem(file.Path);
                if (pathProblem is not null) problems.Add($"File \"{file.Path}\": {pathProblem}");
                else if (!seen.Add(file.Path)) problems.Add($"File \"{file.Path}\" is listed twice.");
                if (file.Sha256 is null || !Sha256Pattern.IsMatch(file.Sha256)) problems.Add($"File \"{file.Path}\" needs a 64-digit SHA-256.");
            }
            if (Entry is null || string.IsNullOrWhiteSpace(Entry.Assembly) || Entry.Type is null || !TypePattern.IsMatch(Entry.Type))
                problems.Add("The entry assembly and a valid entry type name are required.");
            else if (!seen.Contains(Entry.Assembly)) problems.Add($"The entry assembly \"{Entry.Assembly}\" is not one of the listed files.");
        }

        if (Client is null) problems.Add("The client section is missing; use an empty capability list when the module needs nothing from the Client.");
        else
        {
            if (!System.Version.TryParse(Client.MinProtocol, out _) || Client.MinProtocol.Count(c => c == '.') != 1)
                problems.Add("client.minProtocol must look like 1.14.");
            if (Client.Capabilities is null) problems.Add("client.capabilities is missing.");
            else
            {
                foreach (var capability in Client.Capabilities)
                    if (capability is null || !CapabilityPattern.IsMatch(capability)) problems.Add($"\"{capability}\" is not a valid capability name.");
                if (Client.Capabilities.Distinct(StringComparer.Ordinal).Count() != Client.Capabilities.Count) problems.Add("A client capability is listed twice.");
            }
        }
        problems.AddRange(PortProblems(Ports));
        problems.AddRange(SinkProblems(Sinks, Ports));
        problems.AddRange(UploadProblems(Uploads, Ports));
        problems.AddRange(SystemCommandProblems(SystemCommands, Client?.Capabilities));
        return problems;
    }

    /// <summary>Every system command must be a declared capability, listed once.</summary>
    public static IReadOnlyList<string> SystemCommandProblems(IReadOnlyList<string>? systemCommands, IReadOnlyList<string>? capabilities)
    {
        var problems = new List<string>();
        foreach (var command in systemCommands ?? [])
            if (command is null || !(capabilities ?? []).Contains(command, StringComparer.Ordinal)) problems.Add($"The system command \"{command}\" is not one of the declared client capabilities.");
        if ((systemCommands ?? []).Distinct(StringComparer.Ordinal).Count() != (systemCommands ?? []).Count) problems.Add("A system command is listed twice.");
        return problems;
    }

    /// <summary>Upload kinds must be ones this Host knows, listed once, and only a module that asked for both <c>client.upload</c> and <c>storage.source</c> may list any.</summary>
    public static IReadOnlyList<string> UploadProblems(IReadOnlyList<string>? uploads, IReadOnlyList<ModulePortRequest>? ports)
    {
        var problems = new List<string>();
        if ((uploads ?? []).Count > 0)
            foreach (var needed in new[] { ModulePorts.ClientUpload, ModulePorts.StorageSource })
                if (!(ports ?? []).Any(port => port?.Name == needed)) problems.Add($"Uploads are listed but the port \"{needed}\" is not requested.");
        foreach (var upload in uploads ?? [])
            if (upload is null || !ModuleUploads.Known.Contains(upload, StringComparer.Ordinal)) problems.Add($"\"{upload}\" is not a kind of upload this Host offers.");
        if ((uploads ?? []).Distinct(StringComparer.Ordinal).Count() != (uploads ?? []).Count) problems.Add("An upload kind is listed twice.");
        return problems;
    }

    static readonly Regex SinkPattern = new(@"^[a-z][a-z0-9-]{0,31}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Sink kinds are short lower-case names, listed once, and only a module that asked for <c>storage.sink</c> may list any.</summary>
    public static IReadOnlyList<string> SinkProblems(IReadOnlyList<string>? sinks, IReadOnlyList<ModulePortRequest>? ports)
    {
        var problems = new List<string>();
        if ((sinks ?? []).Count > 0 && !(ports ?? []).Any(port => port?.Name == ModulePorts.StorageSink))
            problems.Add("Sinks are listed but the port \"storage.sink\" is not requested.");
        foreach (var sink in sinks ?? [])
            if (sink is null || !SinkPattern.IsMatch(sink)) problems.Add($"\"{sink}\" is not a valid sink name.");
        if ((sinks ?? []).Distinct(StringComparer.Ordinal).Count() != (sinks ?? []).Count) problems.Add("A sink is listed twice.");
        return problems;
    }

    /// <summary>Every rule the requested ports break: a known name, a version this contract has, each port at most once.</summary>
    public static IReadOnlyList<string> PortProblems(IReadOnlyList<ModulePortRequest>? ports)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var port in ports ?? [])
        {
            if (port is null || !ModulePorts.Known.Contains(port.Name ?? "")) problems.Add($"\"{port?.Name}\" is not a port this Host offers.");
            else if (port.Version < 1 || port.Version > ModulePorts.CurrentPortVersion) problems.Add($"The port \"{port.Name}\" is not offered at version {port.Version}.");
            else if (!seen.Add(port.Name!)) problems.Add($"The port \"{port.Name}\" is requested twice.");
        }
        return problems;
    }

    /// <summary>Why a package-relative path is unsafe, or null. Manifests use forward slashes only; the rest of the rule is <see cref="ModulePaths.Problem"/>.</summary>
    internal static string? PathProblem(string? path) =>
        path is not null && path.Contains('\\') ? "use forward slashes and no drive or stream names." : ModulePaths.Problem(path);

    /// <summary>The system path of a listed file under <paramref name="moduleFolder"/>.</summary>
    public static string Combine(string moduleFolder, string relative) =>
        System.IO.Path.Combine(moduleFolder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
}
