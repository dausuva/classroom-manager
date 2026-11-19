// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;

namespace ClassroomManager.Modules;

/// <summary>Finds the installed module folders and checks each one. Nothing is loaded here.</summary>
public static class ModuleDiscovery
{
    /// <summary>
    /// Checks every folder directly under <paramref name="root"/>. A missing root means no modules are installed.
    /// A second folder that claims an id already accepted is refused; the first one in name order wins.
    /// </summary>
    public static IReadOnlyList<ModulePackageResult> Scan(string root, IModuleSignatureVerifier signatures, IReadOnlySet<string> trustedSigners)
    {
        if (!Directory.Exists(root)) return [];
        var results = new List<ModulePackageResult>();
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Directory.EnumerateDirectories(root).Where(path => !ModulePackageStore.IsStoreFolder(Path.GetFileName(path))).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
            {
                results.Add(new ModulePackageResult(folder, null, ["The module folder is a link."]));
                continue;
            }
            var result = ModulePackage.Verify(folder, signatures, trustedSigners);
            if (result.Accepted && !accepted.Add(result.Manifest!.Id))
                result = result with { Problems = [$"Another folder already provides the module \"{result.Manifest.Id}\"."] };
            results.Add(result);
        }
        return results;
    }
}
