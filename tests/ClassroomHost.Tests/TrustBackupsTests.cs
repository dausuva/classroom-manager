// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class TrustBackupsTests : IDisposable
{
    const string Sha17 = "B0F7B1FFD65A0E6E0112E4649BA823475C70E61C88EB328E46BBEA392F8FC28D";
    const string Sha15 = "901052E35369CBD1F353D8D1FFF653D1B0C0015B1C5AF18707E8CE7C494292C5";
    const string Card17 = "C46516BDAFE1", Card15 = "C46516BDB174";

    readonly string _folder = Directory.CreateTempSubdirectory("cm-backups-").FullName;
    string Settings => Path.Combine(_folder, "host.json");
    public void Dispose() => Directory.Delete(_folder, true);

    static JsonObject Doc(string approvalPin, string computerAddress, string theme = "Light") => new()
    {
        ["theme"] = theme,
        ["somethingNewerBuildsAdd"] = "keep me",
        ["trustedTlsEndpoints"] = new JsonArray { new JsonObject { ["address"] = "10.42.4.68", ["port"] = 5050, ["mac"] = Card17, ["certificateSha256"] = new JsonArray { approvalPin } } },
        ["computers"] = new JsonArray { new JsonObject { ["name"] = "410-17", ["address"] = computerAddress, ["clientId"] = "id-17", ["mac"] = Card17 } },
    };

    void Put(string path, JsonObject doc) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, doc.ToJsonString()); }

    [Fact]
    public void Backups_of_every_kind_are_listed_newest_first_with_their_time_and_kind()
    {
        Put(Settings, Doc(Sha17, "10.42.4.68"));
        Put(Settings + ".trust-fix-20261023-100000.bak", Doc(Sha15, "10.42.4.68"));
        Put(Settings + ".trust-enrol-20261023-120000.bak", Doc(Sha15, "10.42.4.68"));
        Put(Settings + ".before-restore-20261022-090000.bak", Doc(Sha15, "10.42.4.68"));
        Put(Path.Combine(_folder, "trust-migration-backup-abc", "host.json"), Doc(Sha15, "10.42.4.68"));
        File.WriteAllText(Settings + ".bak", "an ordinary Host backup is not one of ours");

        var list = TrustBackups.List(Settings);

        Assert.Equal(4, list.Count);
        Assert.Equal(new[] { "trust migration import", "trust-check --from-files", "trust-check --fix", "before an earlier restore" }.Order(), list.Select(b => b.Kind).Order());
        Assert.Equal(list.OrderByDescending(b => b.At).Select(b => b.Path), list.Select(b => b.Path));
        Assert.Equal(new DateTime(2026, 10, 23, 12, 0, 0), list.Single(b => b.Kind == "trust-check --from-files").At);
        Assert.Equal(Path.Combine(_folder, "trust-migration-backup-abc", "host.json"), TrustBackups.SettingsFileOf(list.Single(b => b.Kind == "trust migration import"), Settings));
    }

    [Fact]
    public void A_backup_is_found_by_number_or_path_and_a_wrong_choice_is_a_usage_error()
    {
        Put(Settings, Doc(Sha17, "10.42.4.68"));
        var path = Settings + ".trust-fix-20261023-100000.bak";
        Put(path, Doc(Sha15, "10.42.4.68"));

        Assert.Equal(path, TrustBackups.Find(Settings, "1").Path);
        Assert.Equal(path, TrustBackups.Find(Settings, "\"" + path + "\"").Path);
        Assert.Throws<CliUsageException>(() => TrustBackups.Find(Settings, "2"));
        Assert.Throws<CliUsageException>(() => TrustBackups.Find(Settings, Path.Combine(_folder, "other.json")));
    }

    [Fact]
    public void The_comparison_says_in_plain_lines_what_a_restore_would_change()
    {
        Put(Settings, Doc(Sha17, "10.42.4.99"));
        var backup = Settings + ".trust-fix-20261023-100000.bak";
        Put(backup, Doc(Sha15, "10.42.4.68"));

        var trustOnly = TrustBackups.Compare(Settings, backup, includeComputers: false);
        var withComputers = TrustBackups.Compare(Settings, backup, includeComputers: true);

        Assert.Contains(trustOnly, line => line.StartsWith("Certificate changed back") && line.Contains("901052E35369CBD1"));
        Assert.DoesNotContain(trustOnly, line => line.Contains("Computer"));
        Assert.Contains(withComputers, line => line.Contains("Computer address changed back") && line.Contains("10.42.4.99") && line.Contains("10.42.4.68"));
        Assert.Empty(TrustBackups.Compare(Settings, Settings, true));
    }

    [Fact]
    public void A_restore_puts_back_the_approvals_only_keeps_everything_else_and_copies_the_current_settings_first()
    {
        Put(Settings, Doc(Sha17, "10.42.4.99", theme: "Dark"));
        var backup = Settings + ".trust-fix-20261023-100000.bak";
        Put(backup, Doc(Sha15, "10.42.4.68", theme: "Light"));

        var safety = TrustBackups.Restore(Settings, backup, includeComputers: false, new DateTime(2026, 10, 23, 15, 0, 0));

        var after = JsonNode.Parse(File.ReadAllText(Settings))!.AsObject();
        Assert.Equal(Sha15, after["trustedTlsEndpoints"]![0]!["certificateSha256"]![0]!.GetValue<string>());
        Assert.Equal("10.42.4.99", after["computers"]![0]!["address"]!.GetValue<string>());       // the computers were not asked for
        Assert.Equal("Dark", after["theme"]!.GetValue<string>());                                      // newer settings stay
        Assert.Equal("keep me", after["somethingNewerBuildsAdd"]!.GetValue<string>());                // unknown fields stay
        Assert.EndsWith(".before-restore-20261023-150000.bak", safety);
        Assert.Equal(Sha17, JsonNode.Parse(File.ReadAllText(safety))!["trustedTlsEndpoints"]![0]!["certificateSha256"]![0]!.GetValue<string>());
        Assert.False(File.Exists(Settings + ".restore.tmp"));
    }

    [Fact]
    public void A_restore_with_computers_also_puts_back_the_computer_list_and_can_itself_be_undone()
    {
        Put(Settings, Doc(Sha17, "10.42.4.99"));
        var backup = Settings + ".trust-fix-20261023-100000.bak";
        Put(backup, Doc(Sha15, "10.42.4.68"));

        var safety = TrustBackups.Restore(Settings, backup, includeComputers: true, new DateTime(2026, 10, 23, 15, 0, 0));
        Assert.Equal("10.42.4.68", JsonNode.Parse(File.ReadAllText(Settings))!["computers"]![0]!["address"]!.GetValue<string>());

        TrustBackups.Restore(Settings, safety, includeComputers: true, new DateTime(2026, 10, 23, 15, 5, 0));
        var undone = JsonNode.Parse(File.ReadAllText(Settings))!.AsObject();
        Assert.Equal(Sha17, undone["trustedTlsEndpoints"]![0]!["certificateSha256"]![0]!.GetValue<string>());
        Assert.Equal("10.42.4.99", undone["computers"]![0]!["address"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_command_lists_shows_and_restores()
    {
        var host = new HostSettings { Psk = "k7mq2-xh9vd-3ptwa-e8fzn-4bjrs" };
        host.Save(Settings);
        var backup = Settings + ".trust-fix-20261023-100000.bak";
        File.Copy(Settings, backup);
        host.TrustedTlsEndpoints = [new TrustedTlsEndpoint { Address = "10.42.4.68", Mac = Card17, CertificateSha256 = [Sha17] }];
        host.Save(Settings);

        var list = new StringWriter();
        Assert.Equal(HostCli.ExitOk, await HostCli.RunAsync(["trust-check", "--list-backups", "--settings", Settings], list, new StringWriter()));
        Assert.Contains("trust-check --fix", list.ToString());

        var preview = new StringWriter();
        Assert.Equal(HostCli.ExitOk, await HostCli.RunAsync(["trust-check", "--restore", "1", "--settings", Settings], preview, new StringWriter()));
        Assert.Contains("Approval removed again", preview.ToString());
        Assert.Contains("Nothing was changed", preview.ToString());
        Assert.Single(HostSettings.Load(Settings).TrustedTlsEndpoints);

        var done = new StringWriter();
        Assert.Equal(HostCli.ExitOk, await HostCli.RunAsync(["trust-check", "--restore", "1", "--yes", "--settings", Settings], done, new StringWriter()));
        Assert.Contains("Restored", done.ToString());
        Assert.Empty(HostSettings.Load(Settings).TrustedTlsEndpoints);
        Assert.Single(Directory.GetFiles(_folder, "host.json.before-restore-*.bak"));
    }

    [Fact]
    public void The_restore_options_stand_alone()
    {
        Assert.True(CliParser.Parse(["trust-check", "--list-backups"]).ListBackups);
        Assert.Equal("2", CliParser.Parse(["trust-check", "--restore", "2", "--with-computers", "--yes"]).Restore);
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["trust-check", "--restore", "1", "--fix", "--yes"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["trust-check", "--list-backups", "--restore", "1"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["trust-check", "--with-computers"]));
        Assert.Throws<CliUsageException>(() => CliParser.Parse(["trust-check", "--restore"]));
    }
}
