// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;

namespace ClassroomManager.Host;

/// <summary>Cross-process exclusion on a separate lock file, with flushed atomic replacement of the data file.</summary>
static class JsonRecordFile
{
    public static TResult Use<T, TResult>(string path, Func<T> empty, Func<T, TResult> action, bool write)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var held = Lock(path + ".lock");
        var data = File.Exists(path)
            ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException($"Empty record: {path}")
            : empty();
        var result = action(data);
        if (write) AtomicFile.WriteAllText(path, JsonSerializer.Serialize(data));
        return result;
    }

    static FileStream Lock(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 250) { Thread.Sleep(20); }
        }
    }
}
