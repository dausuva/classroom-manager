// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class SecurityCliTests
{
    [Fact]
    public void A_ping_answer_is_read_into_what_the_report_needs()
    {
        var info = JsonNode.Parse("""
            { "clientId": "6532665a4a5e45f4bad18a2d66b44aaa", "version": "1.18",
              "security": { "counts": { "handshake-failed": 7, "throttled": 2, "log-chain-broken": 1 }, "lastSeq": 12, "lastHash": "0011223344556677" },
              "keyChangedUtc": "2026-09-01T08:30:00Z",
              "hardening": { "legacyDiscovery": true, "requireChannelBinding": false } }
            """)!.AsObject();

        var facts = SecurityCli.FromPing("410-17", info, "synthetic-key-for-the-test");

        Assert.Equal(("410-17", "6532665a4a5e45f4bad18a2d66b44aaa", true, "1.18"), (facts.Name, facts.ClientId, facts.Online, facts.Protocol));
        Assert.Equal(SharedKey.Fingerprint("synthetic-key-for-the-test"), facts.KeyId);
        Assert.Equal((true, false), (facts.LegacyDiscovery, facts.RequiresBinding));
        Assert.Equal((7L, 1L), (facts.FailedHandshakes, facts.BrokenChains));
        Assert.Equal(new DateTime(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc), facts.KeyChangedUtc);
    }

    [Fact]
    public void The_key_epoch_and_the_end_of_the_certificate_are_read_from_the_answer()
    {
        var info = JsonNode.Parse("""{ "clientId": "abc", "version": "1.18", "keyEpoch": 3, "certificateNotAfterUtc": "2046-10-23T10:00:00Z" }""")!.AsObject();

        var facts = SecurityCli.FromPing("410-18", info, "k");

        Assert.Equal(3, facts.KeyEpoch);
        Assert.Equal(new DateTime(2046, 10, 23, 10, 0, 0, DateTimeKind.Utc), facts.CertificateNotAfterUtc);
        Assert.Null(SecurityCli.FromPing("410-19", JsonNode.Parse("""{ "clientId": "abc" }""")!.AsObject(), "k").KeyEpoch);
    }

    [Fact]
    public void An_answer_from_an_older_client_has_nothing_about_hardening_or_events_and_says_so_with_nulls_and_zeros()
    {
        var info = JsonNode.Parse("""{ "clientId": "abc", "version": "1.16" }""")!.AsObject();

        var facts = SecurityCli.FromPing("410-15", info, "k");

        Assert.Equal((null, null), (facts.LegacyDiscovery, facts.RequiresBinding));
        Assert.Equal((0L, 0L), (facts.FailedHandshakes, facts.BrokenChains));
        Assert.Null(facts.KeyChangedUtc);
    }

    [Fact]
    public void The_command_takes_no_target_and_is_in_the_usage_text()
    {
        var options = CliParser.Parse(["security", "--json"]);

        Assert.Equal(("security", true), (options.Command, options.Json));
        Assert.Contains("ClassroomCli security", CliParser.Usage);
    }

    [Fact]
    public async Task With_no_saved_computers_it_prints_the_rows_that_need_none_and_exits_ok()
    {
        var folder = Path.Combine(Path.GetTempPath(), "classroom-seccli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var settings = Path.Combine(folder, "host.json");
            new HostSettings { Psk = "k7mq2-xh9vd-3ptwa-e8fzn-4bjrs" }.Save(settings);
            var output = new StringWriter();

            var exit = await HostCli.RunAsync(["security", "--settings", settings], output, new StringWriter());

            Assert.Equal(HostCli.ExitOk, exit);
            Assert.Contains("Connection security", output.ToString());
            Assert.Contains("Overall: Info", output.ToString());
        }
        finally { Directory.Delete(folder, true); }
    }
}
