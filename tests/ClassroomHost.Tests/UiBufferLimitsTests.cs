// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class UiBufferLimitsTests
{
    [Fact]
    public void Tail_preserves_small_values_and_bounds_large_values()
    {
        Assert.Equal("short", UiBufferLimits.Tail("short", 10));
        Assert.Equal("6789", UiBufferLimits.Tail("0123456789", 4));
        Assert.Equal("", UiBufferLimits.Tail("value", 0));
    }

    [Fact]
    public void Tail_rejects_negative_limits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UiBufferLimits.Tail("value", -1));
    }
}
