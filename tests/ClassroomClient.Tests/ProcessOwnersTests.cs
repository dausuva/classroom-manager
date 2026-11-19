// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.Principal;
using ClassroomManager.Client;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-158: process owners from WTSEnumerateProcessesEx instead of WMI.</summary>
public class ProcessOwnersTests
{
    static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkServiceSid, null);
    static readonly SecurityIdentifier Unknown = new("S-1-5-21-1-2-3-4242");

    [Fact]
    public void Each_owner_is_resolved_once_per_listing()
    {
        var calls = 0;
        var owners = ProcessHandler.MapOwners(
            [(10, System), (11, System), (12, Network), (13, System)],
            sid => { calls++; return sid == System ? @"NT AUTHORITY\SYSTEM" : @"NT AUTHORITY\NETWORK SERVICE"; });

        Assert.Equal(2, calls);
        Assert.Equal(@"NT AUTHORITY\SYSTEM", owners[13]);
        Assert.Equal(@"NT AUTHORITY\NETWORK SERVICE", owners[12]);
    }

    [Fact]
    public void An_owner_that_cannot_be_named_does_not_cost_the_others_theirs()
    {
        var owners = ProcessHandler.MapOwners(
            [(10, System), (11, Unknown), (12, System)],
            sid => sid == Unknown ? throw new IdentityNotMappedException() : @"NT AUTHORITY\SYSTEM");

        Assert.False(owners.ContainsKey(11));
        Assert.Equal(@"NT AUTHORITY\SYSTEM", owners[10]);
        Assert.Equal(@"NT AUTHORITY\SYSTEM", owners[12]);
    }

    [Fact]
    public void A_process_without_an_owner_sid_has_no_owner()
    {
        var owners = ProcessHandler.MapOwners([(10, null), (11, System)], _ => @"NT AUTHORITY\SYSTEM");

        Assert.Equal([11], owners.Keys);
    }

    [Fact]
    public void This_process_is_owned_by_the_current_user()
    {
        var owners = ProcessHandler.ProcessOwners();

        Assert.True(owners.Count > 1);
        Assert.Equal(WindowsIdentity.GetCurrent().Name, owners[Environment.ProcessId], StringComparer.OrdinalIgnoreCase);
    }
}
