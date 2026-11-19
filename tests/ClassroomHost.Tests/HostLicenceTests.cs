// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>The school's licence on the Host (T-213): kept with the Host's files, described in Help > About, never enforced.</summary>
[Collection(HostLicenceGroup.Name)]
public sealed class HostLicenceTests : IDisposable
{
    static readonly DateOnly Agreed = new(2026, 10, 24);
    readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly Dictionary<string, byte[]> _keys;
    readonly string _folder = Path.Combine(Path.GetTempPath(), "cm-licence-" + Guid.NewGuid().ToString("N"));

    public HostLicenceTests()
    {
        var spki = _key.ExportSubjectPublicKeyInfo();
        _keys = new Dictionary<string, byte[]> { [Licences.KeyId(spki)] = spki };
        Directory.CreateDirectory(_folder);
        if (File.Exists(HostLicence.FilePath)) File.Delete(HostLicence.FilePath);
        if (File.Exists(HostLicence.SharedFilePath)) File.Delete(HostLicence.SharedFilePath);
    }

    public void Dispose()
    {
        _key.Dispose();
        if (File.Exists(HostLicence.FilePath)) File.Delete(HostLicence.FilePath);
        if (File.Exists(HostLicence.SharedFilePath)) File.Delete(HostLicence.SharedFilePath);
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    string Licence(int computers = 30, ECDsa? key = null) => Licences.Sign(new LicenceTerms
    {
        Id = "CM-2026-0001", School = "Dausuva Gymnasium", CustomerId = "dausuva-gymnasium", Computers = computers,
        Agreement = Agreed, Ends = Licences.EndFor(Agreed), IssuedUtc = DateTime.UtcNow,
    }, key ?? _key);

    string Write(string text)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + Licences.FileExtension);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void The_licence_is_kept_with_the_hosts_own_files() =>
        Assert.Equal(Path.Combine(HostSettings.DataFolder, "licence.cmlicence"), HostLicence.FilePath);

    [Fact]
    public void Without_a_file_the_host_says_so_and_works_as_before()
    {
        var check = HostLicence.Read(Agreed, _keys);
        Assert.Equal(LicenceState.Missing, check.State);
        var (headline, detail, note) = HostLicence.Describe(check, 12, buildHasKeys: true);
        Assert.Equal("No licence file", headline);
        Assert.Contains("works without one", detail);
        Assert.Equal("", note);
    }

    [Fact]
    public void An_added_licence_is_kept_and_read_back()
    {
        var check = HostLicence.Install(Write(Licence()), Agreed, _keys);
        Assert.Equal(LicenceState.Active, check.State);
        Assert.True(File.Exists(HostLicence.FilePath));
        var read = HostLicence.Read(Agreed, _keys);
        Assert.Equal(LicenceState.Active, read.State);
        var (headline, detail, _) = HostLicence.Describe(read, 30, true);
        Assert.Equal("Licensed to Dausuva Gymnasium", headline);
        Assert.Equal("30 computer(s), until 2027-10-23 (licence CM-2026-0001).", detail);
    }

    [Fact]
    public void A_changed_or_damaged_file_is_refused_and_the_kept_one_stays()
    {
        HostLicence.Install(Write(Licence()), Agreed, _keys);
        var changed = JsonNode.Parse(Licence())!.AsObject();
        changed["licence"]!["computers"] = 45;
        Assert.Contains("changed", Assert.Throws<InvalidDataException>(() => HostLicence.Install(Write(changed.ToJsonString()), Agreed, _keys)).Message);
        Assert.Throws<InvalidDataException>(() => HostLicence.Install(Write("not a licence"), Agreed, _keys));
        Assert.Throws<InvalidDataException>(() => HostLicence.Install(Write(""), Agreed, _keys));
        Assert.Equal(30, HostLicence.Read(Agreed, _keys).Terms!.Computers);
    }

    [Fact]
    public void A_licence_signed_with_a_key_this_version_does_not_know_is_kept_for_later()
    {
        using var newer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var check = HostLicence.Install(Write(Licence(key: newer)), Agreed, _keys);
        Assert.True(check.KeyUnknown);
        Assert.True(File.Exists(HostLicence.FilePath));
        Assert.Equal("Licence from an unknown issuer", HostLicence.Describe(check, 0, buildHasKeys: true).Headline);
        Assert.Equal("Licence not checked", HostLicence.Describe(check, 0, buildHasKeys: false).Headline);
    }

    [Theory]
    [InlineData("2027-09-30", "Licensed to Dausuva Gymnasium", "ends on 2027-10-23 (23 day(s) left)")]
    [InlineData("2027-10-24", "The licence ended on 2027-10-23", "keeps working")]
    public void An_ending_or_ended_licence_says_what_it_means(string today, string headline, string detail)
    {
        var (shownHeadline, shownDetail, _) = HostLicence.Describe(Licences.Check(Licence(), _keys, DateOnly.ParseExact(today, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)), 0, true);
        Assert.Equal(headline, shownHeadline);
        Assert.Contains(detail, shownDetail);
    }

    [Fact]
    public void An_ended_licence_names_the_windows_prompts_and_its_renewal() =>
        Assert.Contains("untrusted-publisher prompts", HostLicence.Describe(Licences.Check(Licence(), _keys, Agreed.AddYears(2)), 0, true).Detail);

    [Fact]
    public void More_computers_than_the_licence_covers_are_noted_but_nothing_is_stopped()
    {
        var check = Licences.Check(Licence(computers: 20), _keys, Agreed);
        Assert.Equal("This Host lists 25 computers; the licence covers 20.", HostLicence.Describe(check, 25, true).Note);
        Assert.Equal("", HostLicence.Describe(check, 20, true).Note);
    }

    [Fact]
    public void An_invalid_licence_shows_why()
    {
        var check = Licences.Check("{\"kind\":\"classroom-manager-licence\"}", _keys, Agreed);
        var (headline, detail, _) = HostLicence.Describe(check, 0, true);
        Assert.Equal("Licence not valid", headline);
        Assert.Equal(check.Problem, detail);
    }
}

/// <summary>Tests that write the Host's licence file; they run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostLicenceGroup
{
    public const string Name = "HostLicence";
}
