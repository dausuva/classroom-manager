// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-194: a healthy fake lab is green on every row, and each deliberate weakening turns exactly its row.</summary>
public sealed class LabSecurityReportTests
{
    const string Master = "k7mq2-xh9vd-3ptwa-e8fzn-4bjrs";
    const string Pin = "B0F7B1FFD65A0E6E0112E4649BA823475C70E61C88EB328E46BBEA392F8FC28D";
    static readonly string[] Ids = ["6532665a4a5e45f4bad18a2d66b44aaa", "11112222333344445555666677778888", "aaaabbbbccccddddeeeeffff00001111"];
    static readonly string[] Cards = ["C46516BDAFE1", "C46516BDB174", "C46516BDB200"];

    static HostSettings Settings()
    {
        var settings = new HostSettings { Psk = Master };
        for (var i = 0; i < 3; i++)
        {
            settings.Computers.Add(new SavedComputer { Name = $"410-{15 + i}", Address = $"10.42.4.{70 + i}", ClientId = Ids[i], Mac = Cards[i] });
            settings.TrustedTlsEndpoints.Add(new TrustedTlsEndpoint { Address = $"10.42.4.{70 + i}", Mac = Cards[i], CertificateSha256 = [Pin] });
        }
        return settings;
    }

    static ComputerSecurity Healthy(int i, HostSettings settings) => new($"410-{15 + i}", Ids[i], true, Protocol.Version,
        SharedKey.Fingerprint(DeviceKeys.Derive(Master, Ids[i])), LegacyDiscovery: false, RequiresBinding: true, KeyChangedUtc: DateTime.UtcNow.AddDays(-40),
        CertificateNotAfterUtc: DateTime.UtcNow.AddDays(400));

    static List<ComputerSecurity> HealthyLab(HostSettings settings) => Enumerable.Range(0, 3).Select(i => Healthy(i, settings)).ToList();

    static LabSecurityRow Row(IReadOnlyList<LabSecurityRow> rows, string title) => rows.Single(row => row.Title == title);

    [Fact]
    public void A_healthy_lab_is_green_on_every_row()
    {
        var settings = Settings();

        var rows = LabSecurityReport.Build(settings, HealthyLab(settings));

        Assert.All(rows, row => Assert.True(row.State == LabState.Ok, $"{row.Title}: {row.State} {row.Detail}"));
        Assert.Equal(LabState.Ok, LabSecurityReport.Overall(rows));
        Assert.Equal(10, rows.Count);
    }

    [Fact]
    public void A_computer_that_missed_the_last_rotation_is_a_warning_inside_the_grace_window_and_a_problem_after_it()
    {
        var settings = Settings();
        settings.KeyEpoch = 3;
        settings.KeyEpochChangedUtc = DateTime.UtcNow.AddDays(-3);
        var lab = HealthyLab(settings).Select((computer, i) => computer with { KeyEpoch = i == 1 ? 2 : 3 }).ToList();

        var waiting = Row(LabSecurityReport.Build(settings, lab), "Key rotation");
        Assert.Equal(LabState.Warning, waiting.State);
        Assert.Contains("410-16", waiting.Detail);
        Assert.DoesNotContain("410-15", waiting.Detail);

        settings.KeyEpochChangedUtc = DateTime.UtcNow.AddDays(-15);
        var expired = Row(LabSecurityReport.Build(settings, lab), "Key rotation");
        Assert.Equal(LabState.Problem, expired.State);
        Assert.Contains("grace window is over", expired.Detail);
    }

    [Fact]
    public void A_removal_of_an_access_key_that_waits_for_a_computer_is_a_warning_and_a_problem_after_the_grace_days()
    {
        var settings = Settings();
        var lab = HealthyLab(settings);
        Assert.DoesNotContain(LabSecurityReport.Build(settings, lab), row => row.Title == "Removed keys");

        KeyRevocations.Record(settings, "Cover teacher", [new ClientInfo { Address = "10.0.0.5", ClientId = "abc" }], DateTime.UtcNow.AddDays(-2));
        var waiting = Row(LabSecurityReport.Build(settings, lab), "Removed keys");
        Assert.Equal(LabState.Warning, waiting.State);
        Assert.Contains("Cover teacher", waiting.Detail);

        settings.PendingRevocations[0].RequestedUtc = DateTime.UtcNow.AddDays(-20);
        Assert.Equal(LabState.Problem, Row(LabSecurityReport.Build(settings, lab), "Removed keys").State);
    }

    [Fact]
    public void Computers_on_the_current_rotation_are_green_and_one_that_is_off_is_only_mentioned()
    {
        var settings = Settings();
        settings.KeyEpoch = 2;
        settings.KeyEpochChangedUtc = DateTime.UtcNow.AddDays(-30);
        var lab = HealthyLab(settings).Select(computer => computer with { KeyEpoch = 2 }).ToList();
        lab[2] = lab[2] with { Online = false, KeyEpoch = null };

        var row = Row(LabSecurityReport.Build(settings, lab), "Key rotation");

        Assert.Equal(LabState.Ok, row.State);
        Assert.Contains("did not answer", row.Detail);
    }

    [Fact]
    public void A_certificate_that_ends_soon_is_a_problem_and_one_that_lasts_twenty_years_is_only_noted()
    {
        var settings = Settings();
        var lab = HealthyLab(settings);
        lab[0] = lab[0] with { CertificateNotAfterUtc = DateTime.UtcNow.AddDays(30) };

        var soon = Row(LabSecurityReport.Build(settings, lab), "Certificate life");
        Assert.Equal(LabState.Problem, soon.State);
        Assert.Contains("410-15", soon.Detail);

        var twenty = HealthyLab(settings).Select(computer => computer with { CertificateNotAfterUtc = DateTime.UtcNow.AddYears(20) }).ToList();
        var noted = Row(LabSecurityReport.Build(settings, twenty), "Certificate life");
        Assert.Equal(LabState.Info, noted.State);
        Assert.Contains("more than ten years", noted.Detail);

        var unknown = HealthyLab(settings).Select(computer => computer with { CertificateNotAfterUtc = null }).ToList();
        Assert.Equal(LabState.Info, Row(LabSecurityReport.Build(settings, unknown), "Certificate life").State);
    }

    [Fact]
    public void Computers_that_report_no_rotation_are_not_blamed_for_it()
    {
        var settings = Settings();
        settings.KeyEpoch = 2;
        settings.KeyEpochChangedUtc = DateTime.UtcNow.AddDays(-30);

        var row = Row(LabSecurityReport.Build(settings, HealthyLab(settings)), "Key rotation");

        Assert.Equal(LabState.Info, row.State);
    }

    [Fact]
    public void A_computer_without_an_approved_certificate_is_a_problem_that_names_it_and_the_command()
    {
        var settings = Settings();
        settings.TrustedTlsEndpoints.RemoveAt(1);

        var rows = LabSecurityReport.Build(settings, HealthyLab(settings));

        var row = Row(rows, "Approved certificates");
        Assert.Equal(LabState.Problem, row.State);
        Assert.Contains("410-16", row.Detail);
        Assert.Contains("trust-check", row.Action);
        Assert.Equal(LabState.Problem, LabSecurityReport.Overall(rows));
    }

    [Fact]
    public void A_computer_still_on_the_lab_key_is_a_warning_with_the_derive_keys_command()
    {
        var settings = Settings();
        var lab = HealthyLab(settings);
        lab[2] = lab[2] with { KeyId = SharedKey.Fingerprint(Master) };

        var row = Row(LabSecurityReport.Build(settings, lab), "A key per computer");

        Assert.Equal(LabState.Warning, row.State);
        Assert.Contains("410-17", row.Detail);
        Assert.Contains("derive-keys", row.Action);
    }

    [Fact]
    public void A_key_older_than_a_year_is_a_warning_and_an_unknown_age_is_information()
    {
        var settings = Settings();
        var lab = HealthyLab(settings);
        lab[1] = lab[1] with { KeyChangedUtc = DateTime.UtcNow.AddDays(-400) };

        var stale = Row(LabSecurityReport.Build(settings, lab), "Key age");
        Assert.Equal(LabState.Warning, stale.State);
        Assert.Contains("410-16", stale.Detail);
        Assert.Contains("Rotate shared key", stale.Action);

        var unknown = lab.Select(computer => computer with { KeyChangedUtc = null }).ToList();
        Assert.Equal(LabState.Info, Row(LabSecurityReport.Build(settings, unknown), "Key age").State);

        lab[1] = lab[1] with { KeyChangedUtc = null };       // one has no date: the others are judged, and the gap is said
        var partly = Row(LabSecurityReport.Build(settings, lab.Select(computer => computer.Name == "410-16" ? computer : computer).ToList()), "Key age");
        Assert.Equal(LabState.Ok, partly.State);
        Assert.Contains("1 computers have not recorded a date", partly.Detail);
    }

    [Fact]
    public void An_old_client_is_a_warning_naming_the_protocol_it_lacks()
    {
        var settings = Settings();
        var lab = HealthyLab(settings);
        lab[0] = lab[0] with { Protocol = "1.16" };

        var row = Row(LabSecurityReport.Build(settings, lab), "Up-to-date Clients");

        Assert.Equal(LabState.Warning, row.State);
        Assert.Contains("410-15", row.Detail);
        Assert.Contains("1.18", row.Detail);
    }

    [Fact]
    public void The_old_discovery_question_is_a_warning_when_every_client_could_do_without_it_and_information_otherwise()
    {
        var settings = Settings();
        var lab = HealthyLab(settings);
        lab[1] = lab[1] with { LegacyDiscovery = true };
        Assert.Equal(LabState.Warning, Row(LabSecurityReport.Build(settings, lab), "Discovery").State);

        lab[0] = lab[0] with { Protocol = "1.16" };       // one Client is too old to do without it: keep the old question for now
        Assert.Equal(LabState.Info, Row(LabSecurityReport.Build(settings, lab), "Discovery").State);
    }

    [Fact]
    public void Clients_that_accept_the_plain_proof_are_information_with_the_setting_to_change()
    {
        var settings = Settings();
        var lab = HealthyLab(settings);
        lab[0] = lab[0] with { RequiresBinding = false };

        var row = Row(LabSecurityReport.Build(settings, lab), "Bound key proof");

        Assert.Equal(LabState.Info, row.State);
        Assert.Contains("requireChannelBinding", row.Action);
    }

    [Fact]
    public void Many_failed_handshakes_are_a_warning_and_a_broken_event_log_is_a_problem()
    {
        var settings = Settings();
        var lab = HealthyLab(settings);
        lab[0] = lab[0] with { FailedHandshakes = 80 };
        Assert.Equal(LabState.Warning, Row(LabSecurityReport.Build(settings, lab), "Security events").State);

        lab[1] = lab[1] with { BrokenChains = 1 };
        var row = Row(LabSecurityReport.Build(settings, lab), "Security events");
        Assert.Equal(LabState.Problem, row.State);
        Assert.Contains("410-16", row.Detail);
    }

    [Fact]
    public void Computers_that_did_not_answer_are_listed_and_not_judged()
    {
        var settings = Settings();
        var lab = HealthyLab(settings);
        lab[2] = lab[2] with { Online = false };

        var rows = LabSecurityReport.Build(settings, lab);

        var row = Row(rows, "Computers not checked");
        Assert.Equal(LabState.Info, row.State);
        Assert.Contains("410-17", row.Detail);
        Assert.Contains("All 2 computers", Row(rows, "A key per computer").Detail);
    }

    [Fact]
    public void With_no_answers_at_all_the_rows_that_need_them_say_they_were_not_checked_and_nothing_is_called_secure()
    {
        var settings = Settings();
        var lab = HealthyLab(settings).Select(computer => computer with { Online = false }).ToList();

        var rows = LabSecurityReport.Build(settings, lab);

        Assert.Equal(LabState.Info, Row(rows, "A key per computer").State);
        Assert.Equal(LabState.Info, Row(rows, "Up-to-date Clients").State);
        Assert.Equal(LabState.Info, Row(rows, "Discovery").State);
        Assert.DoesNotContain(rows, row => row.Title == "Security events" && row.Detail.Contains("no event log is broken"));
    }

    [Fact]
    public void A_settings_file_that_said_tls_was_off_is_reported_as_ignored_and_the_text_report_names_every_row()
    {
        var settings = Settings();
        settings.TlsWasOff = true;

        var rows = LabSecurityReport.Build(settings, HealthyLab(settings));
        var text = LabSecurityReport.Text(rows);

        Assert.Equal(LabState.Info, Row(rows, "Connection security").State);
        Assert.Contains("[OK     ] Approved certificates", text);
        Assert.Contains("Overall: Info", text);
    }
}
