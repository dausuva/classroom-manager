// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClassroomManager.Host;

public enum HostSettingsCommitOutcome { NotCommitted, Committed, RecoveryRequired }

/// <summary>A storage failure with an explicit outcome for the settings/keys pair.</summary>
public sealed class HostSettingsCommitException : IOException
{
    public HostSettingsCommitOutcome Outcome { get; }

    public HostSettingsCommitException(HostSettingsCommitOutcome outcome, Exception error)
        : base(error.Message, error) => Outcome = outcome;
}

/// <summary>Coordinates cooperating settings loads/saves and restores protected key files after a failed settings replacement.</summary>
public static class HostSettingsCommit
{
    public static IDisposable Enter(string path)
    {
        var canonical = Path.GetFullPath(path).ToUpperInvariant();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var mutex = new Mutex(false, @"Local\ClassroomManager.Settings." + identity);
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) throw new TimeoutException(); }
            catch (AbandonedMutexException) { /* Ownership was granted; inspect the files normally. */ }
            return new Lease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    sealed class Lease(Mutex mutex) : IDisposable
    {
        Mutex? _mutex = mutex;
        public void Dispose()
        {
            var held = Interlocked.Exchange(ref _mutex, null);
            if (held is null) return;
            held.ReleaseMutex();
            held.Dispose();
        }
    }

    public static void Write(string path, string json, byte[] protectedKeys, Action<string, byte[]>? write = null)
    {
        write ??= AtomicFile.WriteAllBytes;
        using var lease = Enter(path);
        var keysPath = HostSecretStore.PathFor(path);
        var backupPath = AtomicFile.BackupPath(keysPath);
        // Read both preimages before any write. They stay encrypted in memory and on disk.
        var keysBefore = ReadIfPresent(keysPath);
        var backupBefore = ReadIfPresent(backupPath);
        var settingsBefore = ReadIfPresent(path);
        var next = Encoding.UTF8.GetBytes(json);
        var writingSettings = false;
        try
        {
            write(keysPath, protectedKeys);
            writingSettings = true;
            write(path, next);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A first write can move the primary successfully and then fail to seed its backup.
            // When the previous primary already had identical bytes, byte comparison cannot prove a new commit.
            if (writingSettings && !Equal(settingsBefore, next))
            {
                try
                {
                    if (Equal(ReadIfPresent(path), next))
                        throw new HostSettingsCommitException(HostSettingsCommitOutcome.Committed, error);
                }
                catch (HostSettingsCommitException) { throw; }
                catch (Exception inspect) when (inspect is IOException or UnauthorizedAccessException)
                {
                    throw new HostSettingsCommitException(HostSettingsCommitOutcome.RecoveryRequired,
                        new AggregateException(error, inspect));
                }
            }

            var failures = new List<Exception> { error };
            Restore(keysPath, keysBefore, failures);
            Restore(backupPath, backupBefore, failures);
            throw new HostSettingsCommitException(failures.Count == 1
                ? HostSettingsCommitOutcome.NotCommitted : HostSettingsCommitOutcome.RecoveryRequired,
                failures.Count == 1 ? error : new AggregateException(failures));
        }
    }

    static byte[]? ReadIfPresent(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    static bool Equal(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    static void Restore(string path, byte[]? before, List<Exception> failures)
    {
        try
        {
            if (Equal(ReadIfPresent(path), before)) return;
            if (before is null) File.Delete(path);
            else AtomicFile.ReplaceAllBytes(path, before);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(ex); }
    }
}
