// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>An approved certificate belongs to a computer (its network card), not only to the address it had when it was approved.</summary>
public sealed class TlsPinOwnershipTests
{
    const string Pin15 = "901052E35369CBD1F353D8D1FFF653D1B0C0015B1C5AF18707E8CE7C494292C5";
    const string Pin17 = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";
    const string Card15 = "C46516BDB174", Card17 = "C46516BDAFE1";

    static HostSettings With(params (string Address, string Pin, string Mac)[] entries) => new()
    {
        TrustedTlsEndpoints = entries.Select(entry => new TrustedTlsEndpoint { Address = entry.Address, CertificateSha256 = [entry.Pin], Mac = entry.Mac }).ToList(),
    };

    [Fact]
    public void An_entry_without_a_card_works_by_address_as_before()
    {
        var settings = With(("10.0.0.5", Pin15, ""));

        Assert.Equal([Pin15], settings.TlsPins("10.0.0.5", 5050));
        Assert.Equal([Pin15], settings.TlsPins("10.0.0.5", 5050, Card15));
        Assert.Empty(settings.TlsPins("10.0.0.6", 5050, Card15));
    }

    [Fact]
    public void A_computer_that_moved_is_found_by_its_card_and_the_old_address_is_not_used_for_the_next_computer()
    {
        // 410-15 was approved at .68, then moved to .79; 410-17 now has .68.
        var settings = With(("10.42.4.68", Pin15, Card15), ("10.42.4.79", Pin15, Card15));

        Assert.Equal([Pin15], settings.TlsPins("10.42.4.99", 5050, Card15));
        Assert.Empty(settings.TlsPins("10.42.4.68", 5050, Card17));
    }

    [Fact]
    public void An_unclaimed_old_entry_with_the_certificate_of_another_card_is_not_used_for_a_different_card()
    {
        // The stale entry of 410-17's address has the certificate of 410-15, whose card is known from the other entry.
        var settings = With(("10.42.4.68", Pin15, ""), ("10.42.4.79", Pin15, Card15));

        Assert.Empty(settings.TlsPins("10.42.4.68", 5050, Card17));
        Assert.Equal([Pin15], settings.TlsPins("10.42.4.79", 5050, Card15));
    }

    [Fact]
    public void A_card_with_its_own_approval_keeps_it_when_the_address_holds_another()
    {
        var settings = With(("10.42.4.68", Pin15, Card15), ("10.42.4.50", Pin17, Card17));

        Assert.Equal([Pin17], settings.TlsPins("10.42.4.68", 5050, Card17));
    }

    [Fact]
    public void Claiming_only_names_entries_that_have_no_card_yet()
    {
        var settings = With(("10.0.0.5", Pin15, ""), ("10.0.0.6", Pin17, Card17));

        Assert.True(settings.ClaimTrust("10.0.0.5", 5050, "c4-65-16-bd-b1-74"));
        Assert.False(settings.ClaimTrust("10.0.0.5", 5050, Card15));
        Assert.False(settings.ClaimTrust("10.0.0.6", 5050, Card15));
        Assert.False(settings.ClaimTrust("10.0.0.5", 5050, ""));
        Assert.Equal("C46516BDB174", settings.TrustedTlsEndpoints[0].Mac);
        Assert.Equal(Card17, settings.TrustedTlsEndpoints[1].Mac);
    }

    [Fact]
    public void A_move_takes_the_approvals_of_the_card_to_the_new_address_and_leaves_the_others()
    {
        var settings = With(("10.0.0.5", Pin15, Card15), ("10.0.0.6", Pin17, Card17), ("10.0.0.7", Pin15, ""));

        Assert.True(settings.MoveTrust(Card15, "10.0.0.9", 5050));

        Assert.Equal("10.0.0.9", settings.TrustedTlsEndpoints[0].Address);
        Assert.Equal("10.0.0.6", settings.TrustedTlsEndpoints[1].Address);
        Assert.Equal("10.0.0.7", settings.TrustedTlsEndpoints[2].Address);
        Assert.False(settings.MoveTrust(Card15, "10.0.0.9", 5050));
        Assert.False(settings.MoveTrust("", "10.0.0.9", 5050));
    }

    [Fact]
    public void A_move_to_an_address_that_already_has_an_approval_keeps_that_approval()
    {
        var settings = With(("10.0.0.5", Pin15, Card15), ("10.0.0.9", Pin17, ""));

        settings.MoveTrust(Card15, "10.0.0.9", 5050);

        Assert.Equal(2, settings.TrustedTlsEndpoints.Count);
        Assert.Contains(Pin17, settings.TlsPins("10.0.0.9", 5050));
    }

    [Fact]
    public void An_entry_that_holds_no_certificate_still_counts_as_an_approval_for_its_own_computer_only()
    {
        var settings = new HostSettings
        {
            TrustedTlsEndpoints = [new TrustedTlsEndpoint { Address = "10.0.0.5" }, new TrustedTlsEndpoint { Address = "10.0.0.6", Mac = Card15 }],
        };

        Assert.True(settings.HasTrustEntry("10.0.0.5", 5050, ""));
        Assert.True(settings.HasTrustEntry("10.0.0.5", 5050, Card17));
        Assert.False(settings.HasTrustEntry("10.0.0.6", 5050, Card17));
        Assert.True(settings.HasTrustEntry("10.0.0.99", 5050, Card15));
    }

    [Fact]
    public void A_stale_entry_does_not_stop_the_next_computer_at_the_address_from_being_approved()
    {
        var settings = With(("10.42.4.68", Pin15, ""), ("10.42.4.79", Pin15, Card15));

        Assert.False(settings.HasTrustEntry("10.42.4.68", 5050, Card17));
        Assert.True(settings.HasTrustEntry("10.42.4.79", 5050, Card15));
    }

    [Fact]
    public async Task The_trust_command_records_the_card_and_replaces_only_that_computers_and_unclaimed_entries()
    {
        var folder = Directory.CreateTempSubdirectory("cm-trust-").FullName;
        var path = Path.Combine(folder, "host.json");
        var settings = new HostSettings
        {
            Computers = [new SavedComputer { Address = "10.42.4.68", Name = "410-17", Mac = Card17 }],
            TrustedTlsEndpoints =
            [
                new TrustedTlsEndpoint { Address = "10.42.4.68", CertificateSha256 = [Pin15] },                  // stale, unclaimed
                new TrustedTlsEndpoint { Address = "10.42.4.79", CertificateSha256 = [Pin15], Mac = Card15 },   // another computer's
                new TrustedTlsEndpoint { Address = "10.42.4.68", CertificateSha256 = [Pin15], Mac = "AABBCCDDEEFF" },   // a third card at the address
            ],
        };
        settings.Save(path);

        var exit = await HostCli.RunAsync(["trust", "--computer", "410-17", "--certificate-sha256", Pin17, "--yes", "--settings", path], new StringWriter(), new StringWriter());

        Assert.Equal(HostCli.ExitOk, exit);
        var after = HostSettings.Load(path);
        Assert.Equal([Pin17], after.TlsPins("10.42.4.68", 5050, Card17));
        Assert.Equal([Pin15], after.TlsPins("10.42.4.79", 5050, Card15));
        Assert.Contains(after.TrustedTlsEndpoints, endpoint => endpoint.Mac == "AABBCCDDEEFF");
        Assert.DoesNotContain(after.TrustedTlsEndpoints, endpoint => endpoint.Address == "10.42.4.68" && endpoint.Mac.Length == 0);
        Directory.Delete(folder, true);
    }
}
