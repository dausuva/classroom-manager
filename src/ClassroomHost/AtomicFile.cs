// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text;

namespace ClassroomManager.Host;

static class AtomicFile
{
    public static string BackupPath(string path) => path + ".bak";

    public static void WriteAllText(string path, string contents) =>
        WriteAllBytes(path, Encoding.UTF8.GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] contents)
        => WriteAllBytes(path, contents, keepBackup: true);

    /// <summary>Restores an exact preimage without rotating the paired backup.</summary>
    public static void ReplaceAllBytes(string path, byte[] contents)
        => WriteAllBytes(path, contents, keepBackup: false);

    static void WriteAllBytes(string path, byte[] contents, bool keepBackup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            var backup = BackupPath(path);
            if (!keepBackup) File.Move(temp, path, overwrite: true);
            else if (File.Exists(path)) File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            else
            {
                File.Move(temp, path);
                File.Copy(path, backup, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static void RestoreFromBackup(string path)
    {
        // A program that only reads the Host's files (Classroom Health) uses the backup without putting it back.
        if (HostSettings.SavesSuspended) return;
        var backup = BackupPath(path);
        var temp = path + ".restore-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(backup, temp, overwrite: false);
            File.Move(temp, path, overwrite: true);
            HostBackups.Journal(path, $"could not read {Path.GetFileName(path)}; put the previous copy (.bak) back");
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static void RefreshBackup(string path)
    {
        if (HostSettings.SavesSuspended) return;
        var backup = BackupPath(path);
        var temp = backup + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(path, temp, overwrite: false);
            File.Move(temp, backup, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
