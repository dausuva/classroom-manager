// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;

namespace ClassroomManager.Modules;

/// <summary>The outcome of checking one module folder: an accepted package, or every reason it was refused.</summary>
public sealed record ModulePackageResult(string Folder, ModuleManifest? Manifest, IReadOnlyList<string> Problems)
{
    public bool Accepted => Manifest is not null && Problems.Count == 0;
}

/// <summary>
/// Checks an installed module folder before any of its code is loaded: the manifest, the folder name, every listed file against its
/// SHA-256, that nothing else is in the folder, that no file is a link out of it, and that every program file is signed by a trusted signer.
/// </summary>
public static class ModulePackage
{
    public const string ManifestFile = "module.json";
    public const long MaxFileBytes = 50L * 1024 * 1024;

    static readonly string[] ProgramExtensions = [".dll", ".exe"];

    /// <param name="folder">The module's folder; its name must be the module id.</param>
    /// <param name="signatures">Checks Authenticode signatures.</param>
    /// <param name="trustedSigners">SHA-256 fingerprints (hex) of the leaf certificates whose signatures are accepted.</param>
    public static ModulePackageResult Verify(string folder, IModuleSignatureVerifier signatures, IReadOnlySet<string> trustedSigners)
    {
        var problems = new List<string>();
        var manifestPath = Path.Combine(folder, ManifestFile);
        if (!File.Exists(manifestPath)) return new(folder, null, [$"{ManifestFile} is missing."]);
        if (IsLink(manifestPath)) return new(folder, null, [$"{ManifestFile} is a link."]);
        if (new FileInfo(manifestPath).Length > ModuleManifest.MaxManifestBytes) return new(folder, null, [$"{ManifestFile} is larger than {ModuleManifest.MaxManifestBytes} bytes."]);

        var manifest = ModuleManifest.TryParse(File.ReadAllText(manifestPath), out var parseError);
        if (manifest is null) return new(folder, null, [parseError]);
        problems.AddRange(manifest.Validate());
        if (problems.Count > 0) return new(folder, manifest, problems);

        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.Equals(name, manifest.Id, StringComparison.Ordinal)) problems.Add($"The folder is named \"{name}\" but the module id is \"{manifest.Id}\".");

        var listed = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            if (relative.Equals(ManifestFile, StringComparison.OrdinalIgnoreCase)) continue;
            if (!listed.Contains(relative)) problems.Add($"The folder holds \"{relative}\", which the manifest does not list.");
        }

        foreach (var file in manifest.Files)
        {
            var path = ModuleManifest.Combine(folder, file.Path);
            if (!File.Exists(path)) { problems.Add($"\"{file.Path}\" is listed but missing."); continue; }
            if (HasLinkOnTheWay(folder, path)) { problems.Add($"\"{file.Path}\" is, or lies behind, a link."); continue; }
            if (new FileInfo(path).Length > MaxFileBytes) { problems.Add($"\"{file.Path}\" is larger than {MaxFileBytes} bytes."); continue; }
            if (!HashMatches(path, file.Sha256)) { problems.Add($"\"{file.Path}\" does not match the SHA-256 in the manifest."); continue; }
            if (!ProgramExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;

            var check = signatures.Check(path);
            if (!check.Valid) problems.Add($"\"{file.Path}\": {check.Reason}");
            else if (!trustedSigners.Contains(check.SignerSha256.ToUpperInvariant())) problems.Add($"\"{file.Path}\" is signed by a signer this Host does not trust.");
        }
        return new(folder, manifest, problems);
    }

    static bool HashMatches(string path, string expectedHex)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    /// <summary>True when the file or any folder between it and the module folder is a symbolic link or junction.</summary>
    static bool HasLinkOnTheWay(string root, string file)
    {
        var current = new FileInfo(file);
        if (IsLink(current.FullName)) return true;
        var directory = current.Directory;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        while (directory is not null && directory.FullName.TrimEnd(Path.DirectorySeparatorChar).Length >= rootFull.Length)
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            if (directory.FullName.TrimEnd(Path.DirectorySeparatorChar).Equals(rootFull, StringComparison.OrdinalIgnoreCase)) break;
            directory = directory.Parent;
        }
        return false;
    }
}
