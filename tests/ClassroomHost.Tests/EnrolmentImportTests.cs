// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class EnrolmentImportTests : IDisposable
{
    const string Key = "k7mq2-xh9vd-3ptwa-e8fzn-4bjrs";
    const string Sha17 = "B0F7B1FFD65A0E6E0112E4649BA823475C70E61C88EB328E46BBEA392F8FC28D";
    const string Sha15 = "901052E35369CBD1F353D8D1FFF653D1B0C0015B1C5AF18707E8CE7C494292C5";
    const string Card17 = "C46516BDAFE1", Card15 = "C46516BDB174";
    static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    readonly string _folder = Directory.CreateTempSubdirectory("cm-enrol-").FullName;
    public void Dispose() => Directory.Delete(_folder, true);

    static HostSettings Lab() => new()
    {
        Psk = Key,
        Computers =
        [
            new SavedComputer { Name = "410-17", Address = "10.42.4.68", Mac = Card17, ClientId = "id-17" },
            new SavedComputer { Name = "410-15", Address = "10.42.4.79", Mac = Card15, ClientId = "id-15" },
        ],
    };

    string Write(string name, EnrolmentFile file) { var path = Path.Combine(_folder, name); File.WriteAllText(path, file.ToJson()); return path; }
    static EnrolmentFile File17(string key = Key, DateTime? at = null, string sha = Sha17, string card = Card17, string clientId = "id-17") =>
        EnrolmentFile.Create("410-17", clientId, [card], sha, at ?? Now, key);

    [Fact]
    public void A_good_file_is_approved_for_the_saved_computer_with_its_card_and_changes_nothing_until_applied()
    {
        var settings = Lab();
        Write("enrolment-410-17.json", File17());

        var line = Assert.Single(EnrolmentImport.Plan(settings, _folder, Now));

        Assert.Equal(EnrolmentAction.Enrol, line.Action);
        Assert.Equal("410-17", line.Computer!.Name);
        Assert.Empty(settings.TrustedTlsEndpoints);

        Assert.Equal(1, EnrolmentImport.Apply(settings, [line]));
        var approval = Assert.Single(settings.TrustedTlsEndpoints);
        Assert.Equal(("10.42.4.68", Card17), (approval.Address, approval.Mac));
        Assert.Equal([Sha17], approval.CertificateSha256);
        Assert.Equal([Sha17], settings.TlsPins("10.42.4.99", 5050, Card17));     // found by card, wherever the computer is
    }

    [Fact]
    public void A_file_is_refused_when_the_tag_is_wrong_it_is_old_or_it_is_dated_in_the_future()
    {
        var settings = Lab();
        Write("a-wrong-key.json", File17(key: "someone else's lab key"));
        Write("b-old.json", File17(at: Now.AddDays(-31)));
        Write("c-future.json", File17(at: Now.AddHours(3)));
        Write("d-fresh.json", File17(at: Now.AddDays(-29)));

        var lines = EnrolmentImport.Plan(settings, _folder, Now);

        Assert.Equal([EnrolmentAction.Rejected, EnrolmentAction.Rejected, EnrolmentAction.Rejected, EnrolmentAction.Enrol], lines.Select(line => line.Action));
        Assert.Contains("tag", lines[0].Detail);
        Assert.Contains("30 days", lines[1].Detail);
        Assert.Contains("clock", lines[2].Detail);
    }

    [Fact]
    public void A_file_made_with_a_previous_lab_key_is_still_accepted()
    {
        var settings = Lab();
        settings.PreviousPsks = ["the key before the rotation"];
        Write("old-key.json", File17(key: "the key before the rotation"));

        Assert.Equal(EnrolmentAction.Enrol, Assert.Single(EnrolmentImport.Plan(settings, _folder, Now)).Action);
    }

    [Fact]
    public void An_existing_approval_is_never_replaced_and_the_same_certificate_is_only_acknowledged()
    {
        var settings = Lab();
        settings.TrustedTlsEndpoints = [new TrustedTlsEndpoint { Address = "10.42.4.68", Mac = Card17, CertificateSha256 = [Sha15] }];
        Write("different.json", File17());

        var different = Assert.Single(EnrolmentImport.Plan(settings, _folder, Now));
        Assert.Equal(EnrolmentAction.DifferentApproval, different.Action);
        Assert.Contains("never replaces", different.Detail);
        Assert.Equal(0, EnrolmentImport.Apply(settings, [different]));
        Assert.Equal([Sha15], settings.TlsPins("10.42.4.68", 5050, Card17));

        settings.TrustedTlsEndpoints[0].CertificateSha256 = [Sha17];
        Assert.Equal(EnrolmentAction.AlreadyApproved, Assert.Single(EnrolmentImport.Plan(settings, _folder, Now)).Action);
    }

    [Fact]
    public void A_stale_approval_at_the_address_for_another_card_does_not_block_a_good_file()
    {
        var settings = Lab();
        settings.TrustedTlsEndpoints = [new TrustedTlsEndpoint { Address = "10.42.4.68", Mac = Card15, CertificateSha256 = [Sha15] }];
        Write("410-17.json", File17());

        Assert.Equal(EnrolmentAction.Enrol, Assert.Single(EnrolmentImport.Plan(settings, _folder, Now)).Action);
    }

    [Fact]
    public void A_computer_that_is_not_saved_is_reported_and_not_added()
    {
        var settings = Lab();
        Write("stranger.json", File17(card: "AABBCCDDEEFF", clientId: "id-stranger"));

        var line = Assert.Single(EnrolmentImport.Plan(settings, _folder, Now));

        Assert.Equal(EnrolmentAction.NoSavedComputer, line.Action);
        Assert.Equal(2, settings.Computers.Count);
        Assert.Equal(0, EnrolmentImport.Apply(settings, [line]));
    }

    [Fact]
    public void The_client_id_is_used_only_for_a_saved_computer_with_no_card_and_two_matches_are_never_guessed()
    {
        var settings = Lab();
        settings.Computers[0].Mac = "";
        Write("by-id.json", File17(card: "AABBCCDDEEFF"));
        Assert.Equal(EnrolmentAction.Enrol, Assert.Single(EnrolmentImport.Plan(settings, _folder, Now)).Action);

        settings.Computers.Add(new SavedComputer { Name = "410-99", Address = "10.42.4.99", Mac = Card17 });
        settings.Computers.Add(new SavedComputer { Name = "410-98", Address = "10.42.4.98", Mac = Card17 });
        File.Delete(Path.Combine(_folder, "by-id.json"));
        Write("ambiguous.json", File17());
        var ambiguous = Assert.Single(EnrolmentImport.Plan(settings, _folder, Now));
        Assert.Equal(EnrolmentAction.Rejected, ambiguous.Action);
        Assert.Contains("more than one", ambiguous.Detail);
    }

    [Fact]
    public void Two_files_for_one_computer_leave_only_the_newer()
    {
        var settings = Lab();
        Write("a-old.json", File17(at: Now.AddDays(-3), sha: Sha15));
        Write("b-new.json", File17(at: Now.AddDays(-1)));

        var lines = EnrolmentImport.Plan(settings, _folder, Now);

        Assert.Equal([EnrolmentAction.Rejected, EnrolmentAction.Enrol], lines.Select(line => line.Action));
        Assert.Contains("Superseded", lines[0].Detail);
        Assert.Equal(1, EnrolmentImport.Apply(settings, lines));
        Assert.Equal([Sha17], settings.TlsPins("10.42.4.68", 5050, Card17));
    }

    [Fact]
    public void Junk_in_the_folder_is_reported_per_file_and_a_missing_folder_is_a_usage_error()
    {
        File.WriteAllText(Path.Combine(_folder, "notes.json"), "this is not json");
        File.WriteAllText(Path.Combine(_folder, "other.json"), "{\"format\":\"classroom-enrolment-1\",\"certificateSha256\":\"ABC\"}");

        var lines = EnrolmentImport.Plan(Lab(), _folder, Now);

        Assert.All(lines, line => Assert.Equal(EnrolmentAction.Rejected, line.Action));
        Assert.Equal(2, lines.Count);
        Assert.Throws<CliUsageException>(() => EnrolmentImport.Plan(Lab(), Path.Combine(_folder, "nowhere"), Now));
        Assert.Contains("0 to approve", EnrolmentImport.Text(lines));
    }

    // ---- the command line --------------------------------------------------------------------------------

    [Fact]
    public void From_files_stands_alone_and_only_writes_with_yes()
    {
        var options = CliParser.Parse(["trust-check", "--from-files", _folder]);
        Assert.Equal(_folder, options.FromFiles);
        Assert.False(options.Yes);
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["trust-check", "--from-files", _folder, "--fix", "--yes"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["trust-check", "--from-files", _folder, "--live"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["trust-check", "--from-files"]));
    }

    [Fact]
    public async Task The_command_shows_the_plan_first_and_with_yes_saves_the_approvals_after_a_backup()
    {
        var settingsFolder = Directory.CreateTempSubdirectory("cm-enrol-settings-").FullName;
        try
        {
            var path = Path.Combine(settingsFolder, "host.json");
            Lab().Save(path);
            Write("enrolment-410-17.json", File17(at: DateTime.UtcNow));

            var preview = new StringWriter();
            Assert.Equal(HostCli.ExitOk, await HostCli.RunAsync(["trust-check", "--from-files", _folder, "--settings", path], preview, new StringWriter()));
            Assert.Contains("1 to approve", preview.ToString());
            Assert.Contains("Nothing was changed", preview.ToString());
            Assert.Empty(HostSettings.Load(path).TrustedTlsEndpoints);

            var done = new StringWriter();
            Assert.Equal(HostCli.ExitOk, await HostCli.RunAsync(["trust-check", "--from-files", _folder, "--yes", "--settings", path], done, new StringWriter()));
            Assert.Contains("1 approval(s) made", done.ToString());
            Assert.Equal([Sha17], HostSettings.Load(path).TlsPins("10.42.4.68", 5050, Card17));
            Assert.Single(Directory.GetFiles(settingsFolder, "host.json.trust-enrol-*.bak"));
        }
        finally { Directory.Delete(settingsFolder, true); }
    }
}
