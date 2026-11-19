// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using ClassroomManager.Client;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>
/// These tests replace the static adapter reader of NetInfo, which every connection test that answers a ping reads too. A collection that was only named
/// ran beside those tests and could lend them the fake reader (or lose its one-shot failure to them), so it runs alone.
/// </summary>
[CollectionDefinition("NetInfo", DisableParallelization = true)]
public sealed class NetInfoCollection;

/// <summary>
/// T-161: reading the adapters costs about 150 ms on a Celeron, so ping and the same-machine check share one snapshot that
/// Windows' address-change events drop. The snapshot must never make the same-machine guard follow an old address list.
/// </summary>
[Collection("NetInfo")]
public sealed class NetworkSnapshotTests
{
    static readonly IPAddress First = IPAddress.Parse("192.0.2.5");
    static readonly IPAddress Second = IPAddress.Parse("192.0.2.6");

    static NetInfo.LocalNetwork Network(IPAddress address, string mac = "AABBCCDDEEFF") => new([address], mac);

    static T WithCollector<T>(Func<NetInfo.LocalNetwork?> collect, Func<T> body)
    {
        var previous = NetInfo.Collect;
        NetInfo.Collect = collect;
        NetInfo.Invalidate();
        try { return body(); }
        finally { NetInfo.Collect = previous; NetInfo.Invalidate(); }
    }

    [Fact]
    public void Ping_and_the_guard_read_the_adapters_once_between_changes()
    {
        var reads = 0;
        var mac = WithCollector(() => { reads++; return Network(First); }, () =>
        {
            for (var i = 0; i < 50; i++)
            {
                Assert.True(NetInfo.IsLocalAddress(First));
                Assert.Equal("AABBCCDDEEFF", NetInfo.PrimaryMac());
            }
            return NetInfo.PrimaryMac();
        });
        Assert.Equal(1, reads);
        Assert.Equal("AABBCCDDEEFF", mac);
    }

    [Fact]
    public void An_address_change_is_seen_after_the_snapshot_is_invalidated()
    {
        var current = First;
        WithCollector(() => Network(current), () =>
        {
            Assert.True(NetInfo.IsLocalAddress(First));
            Assert.False(NetInfo.IsLocalAddress(Second));

            current = Second;
            NetInfo.Invalidate();   // what NetworkChange.NetworkAddressChanged does

            Assert.True(NetInfo.IsLocalAddress(Second));
            Assert.False(NetInfo.IsLocalAddress(First));
            return 0;
        });
    }

    [Fact]
    public void A_failed_read_is_not_kept_and_the_guard_still_refuses_to_guess()
    {
        var reads = 0;
        WithCollector(() => reads++ == 0 ? null : Network(First), () =>
        {
            Assert.False(NetInfo.IsLocalAddress(First));   // unreadable: same answer as before the snapshot existed
            Assert.Equal("AABBCCDDEEFF", NetInfo.PrimaryMac());   // the failure was not cached, so this call read again and succeeded
            Assert.True(NetInfo.IsLocalAddress(First));
            return 0;
        });
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Loopback_never_needs_the_adapters()
    {
        var reads = 0;
        WithCollector(() => { reads++; return Network(First); }, () =>
        {
            Assert.True(NetInfo.IsLocalAddress(IPAddress.Loopback));
            Assert.True(NetInfo.IsLocalAddress(IPAddress.IPv6Loopback));
            return 0;
        });
        Assert.Equal(0, reads);
    }
}
