// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>Computer IDs that the tests give to licences (they are not the ID of the PC running the tests).</summary>
static class TestHosts
{
    public const string One = "A1B2-C3D4-E5F6-0718-293A";
    public const string Two = "0000-1111-2222-3333-4444";
    public const string Three = "5555-6666-7777-8888-9999";
    public const string Four = "AAAA-BBBB-CCCC-DDDD-EEEE";
}

/// <summary>A licence is for one Host computer (T-262): the ID, the signed format, the Host's check and the Desk's issuing rules.</summary>
public sealed class ComputerIdTests
{
    [Fact]
    public void The_id_is_twenty_digits_in_groups_of_four_and_the_same_every_time()
    {
        var id = ComputerId.FromMachineGuid("11111111-2222-3333-4444-555555555555");
        Assert.Matches("^[0-9A-F]{4}(-[0-9A-F]{4}){4}$", id);
        Assert.Equal(id, ComputerId.FromMachineGuid(" 11111111-2222-3333-4444-555555555555 "));
        Assert.Equal(id, ComputerId.FromMachineGuid("11111111-2222-3333-4444-555555555555".ToUpperInvariant()));
        Assert.NotEqual(id, ComputerId.FromMachineGuid("11111111-2222-3333-4444-555555555556"));
    }

    [Theory]
    [InlineData("a1b2c3d4e5f60718293a", "A1B2-C3D4-E5F6-0718-293A")]
    [InlineData("A1B2 C3D4 E5F6 0718 293A", "A1B2-C3D4-E5F6-0718-293A")]
    [InlineData(" a1b2-c3d4-e5f6-0718-293a ", "A1B2-C3D4-E5F6-0718-293A")]
    public void Typed_ids_are_read_in_any_spacing_and_case(string typed, string expected) => Assert.Equal(expected, ComputerId.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A1B2-C3D4-E5F6-0718")]
    [InlineData("A1B2-C3D4-E5F6-0718-293A-0000")]
    [InlineData("G1B2-C3D4-E5F6-0718-293A")]
    public void Anything_else_is_not_an_id(string? typed) => Assert.Null(ComputerId.Normalize(typed));

    [Fact]
    public void A_machine_with_no_identity_has_no_id() => Assert.Throws<ArgumentException>(() => ComputerId.FromMachineGuid(" "));
}

public sealed class HostBoundLicenceFormatTests
{
    static readonly DateOnly Agreed = new(2026, 11, 14);
    readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    LicenceTerms Terms(string hostId = "", string hostName = "") => new()
    {
        Id = "CM-2026-0001", School = "Dausuva Gymnasium", CustomerId = "dausuva-gymnasium", Computers = 20,
        Agreement = Agreed, Ends = Licences.EndFor(Agreed), IssuedUtc = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc),
        HostId = hostId, HostName = hostName,
    };

    Dictionary<string, byte[]> Keys()
    {
        var spki = _key.ExportSubjectPublicKeyInfo();
        return new() { [Licences.KeyId(spki)] = spki };
    }

    [Fact]
    public void A_licence_without_a_computer_keeps_exactly_the_text_it_was_signed_with_before_binding()
    {
        var text = System.Text.Encoding.UTF8.GetString(Terms().Canonical());
        Assert.Contains("\"v\":1", text);
        Assert.DoesNotContain("hostId", text);
        var check = Licences.Check(Licences.Sign(Terms(), _key), Keys(), Agreed, TestHosts.One);
        Assert.Equal(LicenceState.Active, check.State);   // an earlier licence still counts on every computer
        Assert.Equal("", check.Terms!.HostId);
    }

    [Fact]
    public void A_licence_for_a_computer_counts_there_and_only_there()
    {
        var file = Licences.Sign(Terms(TestHosts.One, "Room 12 teacher PC"), _key);
        var here = Licences.Check(file, Keys(), Agreed, "a1b2c3d4e5f60718293a");
        Assert.Equal(LicenceState.Active, here.State);
        Assert.True(here.Signed);
        Assert.Equal("Room 12 teacher PC", here.Terms!.HostName);

        var elsewhere = Licences.Check(file, Keys(), Agreed, TestHosts.Two);
        Assert.Equal(LicenceState.Invalid, elsewhere.State);
        Assert.True(elsewhere.WrongComputer);
        Assert.False(elsewhere.Signed);
        Assert.Equal(TestHosts.One, elsewhere.Terms!.HostId);

        // Desk checks files it did not issue without naming a computer.
        Assert.Equal(LicenceState.Active, Licences.Check(file, Keys(), Agreed).State);
    }

    [Fact]
    public void Changing_the_computer_in_the_file_breaks_the_signature()
    {
        var file = JsonNode.Parse(Licences.Sign(Terms(TestHosts.One), _key))!.AsObject();
        file["licence"]!["hostId"] = TestHosts.Two;
        var check = Licences.Check(file.ToJsonString(), Keys(), Agreed, TestHosts.Two);
        Assert.Equal(LicenceState.Invalid, check.State);
        Assert.False(check.WrongComputer);
        Assert.Contains("changed", check.Problem);
    }

    [Fact]
    public void Taking_the_computer_out_of_the_file_breaks_the_signature_too()
    {
        var file = JsonNode.Parse(Licences.Sign(Terms(TestHosts.One), _key))!.AsObject();
        var body = file["licence"]!.AsObject();
        body.Remove("hostId");
        body.Remove("hostName");
        body["v"] = 1;
        Assert.Equal(LicenceState.Invalid, Licences.Check(file.ToJsonString(), Keys(), Agreed, TestHosts.Two).State);
    }

    [Fact]
    public void A_licence_cannot_be_issued_for_something_that_is_not_a_computer_id()
    {
        Assert.NotNull(Licences.Problem(Terms("not-an-id")));
        Assert.NotNull(Licences.Problem(Terms(TestHosts.One, new string('x', 101))));
        Assert.Null(Licences.Problem(Terms(TestHosts.One, "Room 12")));
    }
}

[Collection(HostLicenceGroup.Name)]
public sealed class HostBoundLicenceTests : IDisposable
{
    static readonly DateOnly Agreed = new(2026, 11, 14);
    readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly Dictionary<string, byte[]> _keys;
    readonly string _folder = Path.Combine(Path.GetTempPath(), "cm-bound-" + Guid.NewGuid().ToString("N"));

    public HostBoundLicenceTests()
    {
        var spki = _key.ExportSubjectPublicKeyInfo();
        _keys = new() { [Licences.KeyId(spki)] = spki };
        Directory.CreateDirectory(_folder);
        Clean();
    }

    public void Dispose()
    {
        _key.Dispose();
        Clean();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    static void Clean()
    {
        if (File.Exists(HostLicence.FilePath)) File.Delete(HostLicence.FilePath);
        if (File.Exists(HostLicence.SharedFilePath)) File.Delete(HostLicence.SharedFilePath);
    }

    string Write(string hostId, string name = "", string id = "CM-2026-0001")
    {
        var path = Path.Combine(_folder, id + "-" + Guid.NewGuid().ToString("N")[..6] + Licences.FileExtension);
        File.WriteAllText(path, Licences.Sign(new LicenceTerms
        {
            Id = id, School = "Dausuva Gymnasium", CustomerId = "dausuva-gymnasium", Computers = 30,
            Agreement = Agreed, Ends = Licences.EndFor(Agreed), IssuedUtc = DateTime.UtcNow, HostId = hostId, HostName = name,
        }, _key));
        return path;
    }

    [Fact]
    public void The_id_of_this_computer_comes_from_the_machine_identity_and_is_the_same_for_every_user()
    {
        var before = HostComputerId.ReadMachineGuid;
        try
        {
            HostComputerId.ReadMachineGuid = () => "11111111-2222-3333-4444-555555555555";
            HostComputerId.Reset();
            Assert.Equal(ComputerId.FromMachineGuid("11111111-2222-3333-4444-555555555555"), HostComputerId.Current);
            HostComputerId.ReadMachineGuid = () => null;
            HostComputerId.Reset();
            Assert.Equal("", HostComputerId.Current);
        }
        finally { HostComputerId.ReadMachineGuid = before; HostComputerId.Reset(); }
    }

    [Fact]
    public void A_licence_for_another_computer_is_refused_with_both_ids_and_changes_nothing()
    {
        var ex = Assert.Throws<InvalidDataException>(() => HostLicence.Install(Write(TestHosts.Two, "Room 3"), Agreed, _keys, TestHosts.One));
        Assert.Contains(TestHosts.Two, ex.Message);
        Assert.Contains(TestHosts.One, ex.Message);
        Assert.Contains("Room 3", ex.Message);
        Assert.False(File.Exists(HostLicence.FilePath));
        Assert.False(File.Exists(HostLicence.SharedFilePath));
    }

    [Fact]
    public void A_licence_for_this_computer_is_kept_for_this_user_and_for_every_user_of_the_pc()
    {
        HostLicence.Install(Write(TestHosts.One, "Room 12"), Agreed, _keys, TestHosts.One);
        Assert.True(File.Exists(HostLicence.FilePath));
        Assert.True(File.Exists(HostLicence.SharedFilePath));

        // Another Windows user has no file of their own but reads the PC-wide one.
        File.Delete(HostLicence.FilePath);
        var check = HostLicence.Read(Agreed, _keys, TestHosts.One);
        Assert.Equal(LicenceState.Active, check.State);
        Assert.Equal("Room 12", check.Terms!.HostName);
        Assert.Contains("for this computer (Room 12)", HostLicence.Describe(check, 5, buildHasKeys: true).Detail);
    }

    [Fact]
    public void A_copied_file_for_another_computer_gives_nothing_and_does_not_hide_the_right_one()
    {
        File.Copy(Write(TestHosts.Two), HostLicence.FilePath);   // copied into this user's folder by hand
        var wrong = HostLicence.Read(Agreed, _keys, TestHosts.One);
        Assert.True(wrong.WrongComputer);
        Assert.Equal(LicenceCapacity.FreeComputers, LicenceCapacity.Allowed(officialBuild: true, buildHasKeys: true, wrong));
        Assert.Equal("Licence is for another computer", HostLicence.Describe(wrong, 5, buildHasKeys: true).Headline);

        File.Copy(Write(TestHosts.One), HostLicence.SharedFilePath);
        Assert.Equal(LicenceState.Active, HostLicence.Read(Agreed, _keys, TestHosts.One).State);
    }

    [Fact]
    public void Of_several_files_the_one_for_this_computer_is_kept()
    {
        var files = new[] { Write(TestHosts.Two, id: "CM-2026-0002"), Write(TestHosts.One, id: "CM-2026-0003"), Write(TestHosts.Three, id: "CM-2026-0004") };
        var (installed, check, problem) = HostLicence.InstallFirstMatching(files, Agreed, _keys, TestHosts.One);
        Assert.Equal(files[1], installed);
        Assert.Equal("CM-2026-0003", check!.Terms!.Id);
        Assert.Equal("", problem);

        Clean();
        var (none, _, why) = HostLicence.InstallFirstMatching([files[0], files[2]], Agreed, _keys, TestHosts.One);
        Assert.Null(none);
        Assert.Contains("another computer", why);
        Assert.False(File.Exists(HostLicence.FilePath));
    }

    [Fact]
    public void An_earlier_licence_without_a_computer_still_counts_on_any_computer()
    {
        HostLicence.Install(Write(""), Agreed, _keys, TestHosts.One);
        Assert.Equal(LicenceState.Active, HostLicence.Read(Agreed, _keys, TestHosts.Three).State);
    }
}
