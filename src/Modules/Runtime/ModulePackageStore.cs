// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;

namespace ClassroomManager.Modules;

/// <summary>What an install, removal or rollback did, in plain words.</summary>
/// <param name="Done">True when the change is staged and will apply when the Host starts.</param>
public sealed record ModuleChange(bool Done, string Id, IReadOnlyList<string> Messages);

/// <summary>
/// Installs, updates, removes and rolls back module packages in the modules folder without ever leaving it half changed.
/// A change is checked first (the whole package, signatures included) and staged under <c>.pending</c>; it takes effect when the
/// Host next starts, before any module code is loaded, because a running Host holds its module files open. The folder that was
/// replaced is kept under <c>.previous</c>, so one step back is always possible. Folders whose names start with a dot are not modules.
/// </summary>
public static class ModulePackageStore
{
    public const string PendingFolder = ".pending";
    public const string PreviousFolder = ".previous";
    const string RemoveMark = ".remove";

    static string Pending(string root) => Path.Combine(root, PendingFolder);
    static string Previous(string root) => Path.Combine(root, PreviousFolder);

    /// <summary>True for a folder name that belongs to the store and not to a module.</summary>
    public static bool IsStoreFolder(string name) => name.StartsWith('.');

    /// <summary>Checks a package folder and stages it; nothing under the live modules changes yet.</summary>
    public static ModuleChange Stage(string sourceFolder, string root, IModuleSignatureVerifier signatures, IReadOnlySet<string> trustedSigners, bool allowOlder = false)
    {
        sourceFolder = sourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(sourceFolder)) return new(false, "", ["The folder does not exist."]);

        var check = ModulePackage.Verify(sourceFolder, signatures, trustedSigners);
        if (!check.Accepted) return new(false, check.Manifest?.Id ?? "", check.Problems);
        var manifest = check.Manifest!;

        var installed = Path.Combine(root, manifest.Id);
        if (Directory.Exists(installed) && ModulePackage.Verify(installed, signatures, trustedSigners) is { Accepted: true } current)
        {
            var older = System.Version.Parse(manifest.Version).CompareTo(System.Version.Parse(current.Manifest!.Version));
            if (older < 0 && !allowOlder) return new(false, manifest.Id, [$"Version {current.Manifest.Version} is installed; {manifest.Version} is older. Remove or roll back instead, or ask for the older one explicitly."]);
            if (older == 0) return new(false, manifest.Id, [$"Version {manifest.Version} is already installed."]);
        }

        Directory.CreateDirectory(Pending(root));
        var work = Path.Combine(Pending(root), Guid.NewGuid().ToString("N"));
        try
        {
            // Copied, then checked again where it will live: what is staged is what was verified, not what the source folder holds later.
            var copy = Path.Combine(work, manifest.Id);
            CopyTree(sourceFolder, copy);
            var again = ModulePackage.Verify(copy, signatures, trustedSigners);
            if (!again.Accepted) return new(false, manifest.Id, again.Problems);

            var target = Path.Combine(Pending(root), manifest.Id);
            if (Directory.Exists(target)) Directory.Delete(target, true);   // an earlier staged copy of this same module, replaced by the newer request
            foreach (var mark in new[] { RemoveMark, ".rollback" })
                File.Delete(target + mark);
            Directory.Move(copy, target);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
        return new(true, manifest.Id, [$"{manifest.Name} {manifest.Version} is checked and will be installed when the Host starts."]);
    }

    /// <summary>Stages the removal of an installed module; it is kept under <c>.previous</c>.</summary>
    public static ModuleChange StageRemoval(string id, string root)
    {
        if (!Directory.Exists(Path.Combine(root, id))) return new(false, id, ["That module is not installed."]);
        Directory.CreateDirectory(Pending(root));
        File.WriteAllText(Path.Combine(Pending(root), id + RemoveMark), "");
        return new(true, id, [$"{id} will be removed when the Host starts. It can be brought back with a rollback."]);
    }

    /// <summary>Stages putting the kept previous version back.</summary>
    public static ModuleChange StageRollback(string id, string root)
    {
        if (!Directory.Exists(Path.Combine(Previous(root), id))) return new(false, id, ["There is no earlier version of that module to go back to."]);
        Directory.CreateDirectory(Pending(root));
        File.WriteAllText(Path.Combine(Pending(root), id + ".rollback"), "");
        return new(true, id, [$"{id} will go back to its previous version when the Host starts."]);
    }

    /// <summary>
    /// Applies what is staged. Call it before the modules folder is scanned. Each module is swapped on its own: the old folder moves to
    /// <c>.previous</c> first and moves back if the new one cannot be put in place, so a failure leaves that module as it was.
    /// </summary>
    public static IReadOnlyList<string> ApplyPending(string root)
    {
        var report = new List<string>();
        var pending = Pending(root);
        if (!Directory.Exists(pending)) return report;

        foreach (var entry in Directory.EnumerateFileSystemEntries(pending).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList())
        {
            var name = Path.GetFileName(entry);
            try
            {
                if (name.EndsWith(RemoveMark, StringComparison.Ordinal)) { Remove(root, name[..^RemoveMark.Length]); File.Delete(entry); report.Add($"Removed {name[..^RemoveMark.Length]}."); }
                else if (name.EndsWith(".rollback", StringComparison.Ordinal)) { Rollback(root, name[..^".rollback".Length]); File.Delete(entry); report.Add($"Rolled back {name[..^".rollback".Length]}."); }
                else if (Directory.Exists(entry) && !Guid.TryParseExact(name, "N", out _)) { Replace(root, name, entry); report.Add($"Installed {name}."); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Add($"Could not apply the change for {name}: {ex.Message} It stays staged and is tried again at the next start.");
            }
        }
        return report;
    }

    static void Replace(string root, string id, string staged)
    {
        var live = Path.Combine(root, id);
        var kept = Path.Combine(Previous(root), id);
        Directory.CreateDirectory(Previous(root));
        var hadLive = Directory.Exists(live);
        if (hadLive)
        {
            if (Directory.Exists(kept)) Directory.Delete(kept, true);   // only one step back is kept
            Directory.Move(live, kept);
        }
        try { Directory.Move(staged, live); }
        catch
        {
            if (hadLive && Directory.Exists(kept) && !Directory.Exists(live)) Directory.Move(kept, live);
            throw;
        }
    }

    static void Remove(string root, string id)
    {
        var live = Path.Combine(root, id);
        if (!Directory.Exists(live)) return;
        var kept = Path.Combine(Previous(root), id);
        Directory.CreateDirectory(Previous(root));
        if (Directory.Exists(kept)) Directory.Delete(kept, true);
        Directory.Move(live, kept);
    }

    static void Rollback(string root, string id)
    {
        var kept = Path.Combine(Previous(root), id);
        if (!Directory.Exists(kept)) return;
        var live = Path.Combine(root, id);
        var swap = Path.Combine(Previous(root), id + ".swap");
        if (Directory.Exists(live)) Directory.Move(live, swap);
        try { Directory.Move(kept, live); }
        catch
        {
            if (Directory.Exists(swap) && !Directory.Exists(live)) Directory.Move(swap, live);
            throw;
        }
        if (Directory.Exists(swap)) Directory.Move(swap, kept);   // what was live is now the one step back, so a rollback can be undone
    }

    static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: false);
    }
}
