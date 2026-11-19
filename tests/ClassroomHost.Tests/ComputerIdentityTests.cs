// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ComputerIdentityTests
{
    [Theory]
    [InlineData("00:11:22:aa:bb:cc", "001122AABBCC")]
    [InlineData("00-11-22-AA-BB-CC", "001122AABBCC")]
    [InlineData("invalid", "")]
    public void NormalizeMac_produces_a_canonical_hardware_address(string input, string expected) =>
        Assert.Equal(expected, ComputerIdentity.NormalizeMac(input));

    [Fact]
    public void StableMatch_prefers_mac_and_uses_installation_id_when_mac_is_unavailable()
    {
        Assert.False(ComputerIdentity.StableMatch("cloned-id", "001122AABBCC", "cloned-id", "AABBCCDDEEFF"));
        Assert.True(ComputerIdentity.StableMatch("client-1", "", "client-1", "AABBCCDDEEFF"));
        Assert.True(ComputerIdentity.StableMatch("old-install", "00:11:22:AA:BB:CC", "new-install", "001122aabbcc"));
    }

    [Fact]
    public void SafeAddressMatch_rejects_a_reused_dhcp_address_with_conflicting_identities()
    {
        var remembered = new ClientInfo
        {
            Address = "192.168.1.20",
            ClientId = "client-1",
            Mac = "001122AABBCC",
        };

        Assert.False(ComputerIdentity.SafeAddressMatch(
            remembered, "192.168.1.20", "client-2", "AABBCCDDEEFF"));
    }

    [Fact]
    public void SafeAddressMatch_supports_legacy_records_that_have_no_identity_yet()
    {
        var remembered = new ClientInfo { Address = "192.168.1.20" };

        Assert.True(ComputerIdentity.SafeAddressMatch(
            remembered, "192.168.1.20", "client-1", "001122AABBCC"));
    }
}
