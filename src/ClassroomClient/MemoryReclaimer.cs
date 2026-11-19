// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Runtime;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Gives memory back to Windows after the two known bursts: loading a translation at start-up, and a
/// large Base64 file transfer. Measured 2026-10-22 (tasks/2026-10-23-client-memory-footprint.md): a
/// forced compacting collection keeps what the burst committed, an aggressive one returns it.
/// </summary>
static class MemoryReclaimer
{
    internal const long LargeTransferBytes = 8L * 1024 * 1024;
    static readonly object Gate = new();
    static CancellationTokenSource? _pending;

    /// <summary>
    /// After <c>Loc.Use</c> at start-up: parsing the built-in translation commits about 10 MB that the GC
    /// would otherwise keep. Nothing to do on English Windows, where no translation is loaded.
    /// </summary>
    public static void ReleaseAfterStartup(Translation loaded)
    {
        if (!ShouldReleaseAfterStartup(loaded)) return;
        var (before, after) = GiveBack();
        Log.Write($"memory: released after loading the {loaded.Language} translation; private memory {before / 1048576d:0.0} -> {after / 1048576d:0.0} MB.");
    }

    internal static bool ShouldReleaseAfterStartup(Translation loaded) => loaded.Count > 0;

    public static void NoteTransferActivity(long bytes, bool complete)
    {
        if (bytes < LargeTransferBytes) return;
        if (complete)
        {
            ScheduleAfterLargeTransfer(bytes);
            return;
        }

        lock (Gate)
        {
            _pending?.Cancel();
            _pending = null;
        }
    }

    public static void ScheduleAfterLargeTransfer(long bytes)
    {
        if (bytes < LargeTransferBytes) return;
        CancellationTokenSource pending;
        lock (Gate)
        {
            _pending?.Cancel();
            pending = new CancellationTokenSource();
            _pending = pending;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), pending.Token);
                var (before, after) = GiveBack();
                Log.Write($"memory: reclaimed after {bytes / 1048576d:0.0} MB transfer; private memory {before / 1048576d:0.0} -> {after / 1048576d:0.0} MB.");
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (Gate)
                {
                    if (ReferenceEquals(_pending, pending)) _pending = null;
                }
                pending.Dispose();
            }
        });
    }

    /// <summary>
    /// One blocking, compacting full collection that also decommits the memory it frees
    /// (<see cref="GCCollectionMode.Aggressive"/>). Returns the private memory before and after, for the log.
    /// </summary>
    internal static (long Before, long After) GiveBack()
    {
        var before = PrivateBytes();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        return (before, PrivateBytes());
    }

    static long PrivateBytes()
    {
        using var self = Process.GetCurrentProcess();
        return self.PrivateMemorySize64;
    }
}
