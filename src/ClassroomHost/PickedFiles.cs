// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using ClassroomManager.Modules;

namespace ClassroomManager.Host;

/// <summary>
/// The files the teacher picked for one module run. A module is given a handle (<see cref="ModulePickedFile"/>), never the path; this class keeps
/// the path and answers only for handles it issued.
/// </summary>
public sealed class PickedFiles : IModulePickedFiles
{
    readonly Dictionary<string, ModulePickedFileEntry> _files = new(StringComparer.Ordinal);

    /// <summary>Records a file the teacher picked and returns its handle. Throws when the file cannot be sent as a picture.</summary>
    public async Task<ModulePickedFile> PickPictureAsync(string path, CancellationToken cancellation = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The picture was not found.", path);
        if (ModuleUploads.Problem(ModuleUploads.Wallpaper, info.Name, info.Length) is { } problem) throw new InvalidOperationException(problem);

        string hash;
        await using (var stream = File.OpenRead(path))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)).ToLowerInvariant();
        var picked = new ModulePickedFile(Guid.NewGuid().ToString("N"), info.Name, info.Length, hash);
        lock (_files) _files[picked.Id] = new(info.FullName, picked);
        return picked;
    }

    public ModulePickedFileEntry? Find(string sourceId)
    {
        lock (_files) return sourceId is not null && _files.TryGetValue(sourceId, out var entry) ? entry : null;
    }
}
