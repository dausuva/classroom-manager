// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Client;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class AudioEndpointsTests
{
    /// <summary>
    /// Read-only check that the hand-declared Core Audio COM interfaces line up with Windows: a wrong
    /// method slot fails or crashes here rather than on a student PC. It changes nothing, and passes
    /// with zero endpoints on a machine that has no audio devices.
    /// </summary>
    [Fact]
    public void Active_endpoints_can_be_listed_with_their_mute_state()
    {
        var endpoints = AudioEndpoints.List();

        Assert.All(endpoints, endpoint => Assert.StartsWith("{", endpoint.Id));
        Assert.Equal(endpoints.Count, endpoints.Select(endpoint => endpoint.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Unmuting_nothing_does_not_touch_any_device()
    {
        var before = AudioEndpoints.List();

        AudioEndpoints.Unmute([]);

        Assert.Equal(before, AudioEndpoints.List());
    }
}
