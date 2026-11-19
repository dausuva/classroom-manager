// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Client;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

[CollectionDefinition(nameof(GcSensitive), DisableParallelization = true)]
public sealed class GcSensitive { }

/// <summary>T-155 and T-156: memory given back after start-up and after large transfers.</summary>
[Collection(nameof(GcSensitive))]
public class MemoryReclaimerTests
{
    [Fact]
    public void English_windows_skips_the_start_up_collection()
    {
        Assert.False(MemoryReclaimer.ShouldReleaseAfterStartup(Translation.None));
    }

    [Fact]
    public void A_loaded_translation_is_followed_by_a_collection()
    {
        Assert.True(MemoryReclaimer.ShouldReleaseAfterStartup(Loc.BuiltIn(Loc.Lithuanian)));
    }

    [Fact]
    public void Give_back_returns_freed_memory_to_windows()
    {
        const int megabytes = 64;
        Hold(megabytes);
        var withGarbage = GC.GetGCMemoryInfo(GCKind.Any).TotalCommittedBytes;

        MemoryReclaimer.GiveBack();

        var after = GC.GetGCMemoryInfo(GCKind.Any).TotalCommittedBytes;
        // An ordinary forced collection compacts but keeps most of this committed; the aggressive one decommits it.
        Assert.True(withGarbage - after > megabytes / 2 * 1048576L,
            $"committed {withGarbage / 1048576d:0.0} MB before, {after / 1048576d:0.0} MB after");
    }

    // Allocates and keeps the objects alive until a collection has recorded the committed size, then drops them.
    static void Hold(int megabytes)
    {
        var chunks = new List<byte[]>(megabytes * 64);
        for (var i = 0; i < megabytes * 64; i++) chunks.Add(new byte[16 * 1024]);
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        GC.KeepAlive(chunks);
    }
}
