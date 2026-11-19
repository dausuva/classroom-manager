// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;

namespace ClassroomManager.Client;

/// <summary>
/// Limits where a Teacher key may upload. Uploads land in the staging folder, which also holds
/// installers an administrator is about to run elevated; if a Teacher key could overwrite those it
/// could run code as SYSTEM. So a Teacher key may write only inside a staging folder it created
/// itself (see <see cref="OwnedFolder"/> for what counts as one): the first key to write into a folder
/// that does not exist yet becomes its owner, and a folder that already exists without an owner
/// belongs to an administrator.
/// </summary>
sealed class StagingOwnership
{
    readonly string _root;
    readonly string _ownersPath;
    readonly object _gate = new();

    public StagingOwnership(string stagingDir, string ownersPath)
    {
        _root = Path.GetFullPath(stagingDir).TrimEnd('\\') + "\\";
        _ownersPath = ownersPath;
    }

    public bool MayWrite(string keyName, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return false;

        var folder = OwnedFolder(full[_root.Length..]);
        if (folder is null) return false;

        lock (_gate)
        {
            var owners = Load();
            if (owners.TryGetValue(folder, out var owner))
                return owner.Equals(keyName, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(Path.Combine(_root, folder))) return false;
            owners[folder] = keyName;
            Save(owners);
            return true;
        }
    }

    /// <summary>True when the path is inside a staging folder this key created. Never makes the key an owner.</summary>
    public bool Owns(string keyName, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return false;
        var folder = OwnedFolder(full[_root.Length..]);
        if (folder is null) return false;
        lock (_gate)
            return Load().TryGetValue(folder, out var owner) && owner.Equals(keyName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The folder a key can own for a path below the staging root, or null when the path is not inside
    /// one. Each Host uploads into its own "Hosts\&lt;host id&gt;" folder, so that pair is the unit of
    /// ownership; "Hosts" itself is shared by every Host and can never belong to one key. Any other
    /// first-level folder is a unit of its own.
    /// </summary>
    static string? OwnedFolder(string relative)
    {
        var parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var depth = parts.Length > 0 && parts[0].Equals("Hosts", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        // There must be a file name below the owned folder: nothing may be written beside it.
        return parts.Length > depth ? string.Join('\\', parts.Take(depth)) : null;
    }

    Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_ownersPath))
                return new Dictionary<string, string>(
                    JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_ownersPath)) ?? new(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException) { /* unreadable: treat every existing folder as an administrator's */ }
        catch (IOException) { }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    void Save(Dictionary<string, string> owners)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_ownersPath)!);
        File.WriteAllText(_ownersPath, JsonSerializer.Serialize(owners));
    }
}
