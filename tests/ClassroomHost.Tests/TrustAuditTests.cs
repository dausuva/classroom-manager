// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class TrustAuditTests
{
    const string Pin15 = "901052E35369CBD1F353D8D1FFF653D1B0C0015B1C5AF18707E8CE7C494292C5";
    const string Pin17 = "B0F7B1FFD65A0E6E0112E4649BA823475C70E61C88EB328E46BBEA392F8FC28D";
    const string Card15 = "C46516BDB174", Card17 = "C46516BDAFE1";

    static SavedComputer Pc(string name, string address, string mac) => new() { Name = name, Address = address, Mac = mac };
    static TrustedTlsEndpoint Entry(string address, string pin, string mac = "") => new() { Address = address, CertificateSha256 = [pin], Mac = mac };
    static AuditProbe Probe(string address, string presented, string arp = "") => new(address, 5050, presented, arp);

    static HostSettings Lab(params TrustedTlsEndpoint[] entries) => new()
    {
        Computers = [Pc("410-17", "10.42.4.68", Card17), Pc("410-15", "10.42.4.79", Card15)],
        TrustedTlsEndpoints = entries.ToList(),
    };

    [Fact]
    public void A_swapped_address_is_found_the_stale_approval_is_dropped_and_the_new_computer_waits_for_a_person()
    {
        var settings = Lab(Entry("10.42.4.68", Pin15), Entry("10.42.4.79", Pin15, Card15));

        var report = TrustAudit.Analyze(settings, [], [Probe("10.42.4.68", Pin17, Card17), Probe("10.42.4.79", Pin15, Card15)], live: true);

        Assert.Equal(AuditState.NeedsApproval, report.Lines.Single(line => line.Computer.Name == "410-17").State);
        Assert.Contains(Pin17[..20], report.Lines.Single(line => line.Computer.Name == "410-17").Command);
        Assert.Equal(AuditState.Ok, report.Lines.Single(line => line.Computer.Name == "410-15").State);
        var remove = Assert.IsType<RemoveFix>(Assert.Single(report.Fixes));
        Assert.Equal("10.42.4.68", remove.Endpoint.Address);

        Assert.Equal(1, TrustAudit.Apply(settings, report.Fixes));
        Assert.DoesNotContain(settings.TrustedTlsEndpoints, entry => entry.Address == "10.42.4.68");
    }

    [Fact]
    public void A_certificate_that_is_approved_for_another_address_is_named()
    {
        var settings = Lab(Entry("10.42.4.68", Pin15), Entry("10.42.4.63", Pin17));

        var line = TrustAudit.Analyze(settings, [], [Probe("10.42.4.68", Pin17), Probe("10.42.4.79", "")], live: true).Lines[0];

        Assert.Equal(AuditState.CertOfAnother, line.State);
        Assert.Contains("10.42.4.63", line.Detail);
    }

    [Fact]
    public void An_address_that_another_card_holds_is_a_clash_and_names_both_computers_and_where_the_moved_one_is()
    {
        var settings = Lab(Entry("10.42.4.79", Pin15, Card15));
        var sightings = new[] { new AuditSighting("10.42.4.68", Card15, "", "410-15"), new AuditSighting("10.42.4.99", Card17, "", "410-17") };

        var report = TrustAudit.Analyze(settings, sightings, [Probe("10.42.4.68", Pin15, Card15)], live: true);

        var line = report.Lines.Single(l => l.Computer.Name == "410-17");
        Assert.Equal(AuditState.AddressClash, line.State);
        Assert.Contains("410-15", line.Detail);
        Assert.Contains("10.42.4.99", line.Detail);
        // Both computers moved: 410-17 to .99 and 410-15 into the address 410-17 had.
        Assert.Equal(["10.42.4.99", "10.42.4.68"], report.Fixes.Cast<MoveFix>().Select(move => move.NewAddress));
    }

    [Fact]
    public void A_computer_that_moved_is_followed_with_its_approval()
    {
        var settings = Lab(Entry("10.42.4.68", Pin17, Card17), Entry("10.42.4.79", Pin15, Card15));
        var report = TrustAudit.Analyze(settings, [new AuditSighting("10.42.4.99", Card17, "", "410-17")],
            [Probe("10.42.4.68", ""), Probe("10.42.4.79", Pin15, Card15)], live: true);

        Assert.Equal(AuditState.MovedAddress, report.Lines[0].State);
        Assert.Equal(1, TrustAudit.Apply(settings, report.Fixes));

        Assert.Equal("10.42.4.99", settings.Computers[0].Address);
        Assert.Equal([Pin17], settings.TlsPins("10.42.4.99", 5050, Card17));
    }

    [Fact]
    public void An_approval_that_matches_but_names_no_card_is_claimed_and_a_computer_that_is_off_is_only_reported()
    {
        var settings = Lab(Entry("10.42.4.68", Pin17));

        var report = TrustAudit.Analyze(settings, [], [Probe("10.42.4.68", Pin17, Card17), Probe("10.42.4.79", "")], live: true);

        Assert.Equal(AuditState.Ok, report.Lines[0].State);
        Assert.Equal(AuditState.Offline, report.Lines[1].State);
        Assert.Equal(1, TrustAudit.Apply(settings, report.Fixes));
        Assert.Equal(Card17, settings.TrustedTlsEndpoints[0].Mac);
    }

    [Fact]
    public void Without_looking_at_the_network_nothing_is_judged_but_double_addresses_and_orphans_are_noted()
    {
        var settings = Lab(Entry("10.42.4.68", Pin17), Entry("10.9.9.9", Pin15));
        settings.Computers.Add(Pc("410-99", "10.42.4.68", ""));

        var report = TrustAudit.Analyze(settings, [], [], live: false);

        Assert.All(report.Lines, line => Assert.Equal(AuditState.NotChecked, line.State));
        Assert.Contains(report.Notes, note => note.Contains("both saved at 10.42.4.68"));
        Assert.Contains(report.Notes, note => note.Contains("10.9.9.9") && note.Contains("belongs to no saved computer"));
        Assert.Empty(report.Fixes);
    }

    [Fact]
    public void Approving_needs_the_start_of_the_fingerprint_that_was_read_on_the_computer()
    {
        var settings = Lab(Entry("10.42.4.68", Pin15));
        var computer = settings.Computers[0];

        Assert.Throws<CliUsageException>(() => TrustAudit.Approve(settings, computer, Pin17, Pin17[..10]));
        Assert.Throws<InvalidOperationException>(() => TrustAudit.Approve(settings, computer, Pin17, Pin15[..24]));
        Assert.Throws<InvalidOperationException>(() => TrustAudit.Approve(settings, computer, "", Pin17[..24]));
        Assert.Equal([Pin15], settings.TlsPins("10.42.4.68", 5050));   // nothing changed by the refusals

        var approved = TrustAudit.Approve(settings, computer, Pin17, string.Join(' ', Enumerable.Range(0, 6).Select(i => Pin17.Substring(i * 4, 4))));

        Assert.Equal(Pin17, approved);
        Assert.Equal([Pin17], settings.TlsPins("10.42.4.68", 5050, Card17));
        Assert.Equal(Card17, settings.TrustedTlsEndpoints.Single(entry => entry.Address == "10.42.4.68").Mac);
    }

    [Fact]
    public void The_command_line_accepts_trust_check_and_guards_the_two_commands_that_write()
    {
        Assert.True(CliParser.Parse(["trust-check", "--live", "--json"]).Live);
        Assert.True(CliParser.Parse(["trust-check", "--fix", "--yes"]).Fix);
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["trust-check", "--fix"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["trust-check", "--approve", "--computer", "a", "--yes"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["trust-check", "--approve", "--check", "ABCD", "--yes"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["trust-check", "--check", "ABCD"]));
        var approve = CliParser.Parse(["trust-check", "--approve", "--computer", "410-17", "--check", "B0F7B1FF", "--yes"]);
        Assert.True(approve.Approve);
        Assert.Equal("B0F7B1FF", approve.Check);
    }

    [Fact]
    public void The_router_table_lists_name_card_and_address_for_every_computer()
    {
        var table = TrustAudit.Reservations(Lab());

        Assert.Equal($"410-15\tC4-65-16-BD-B1-74\t10.42.4.79{Environment.NewLine}410-17\tC4-65-16-BD-AF-E1\t10.42.4.68", table);
    }
}
