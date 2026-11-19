// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>Licences (T-213): what may be issued, the signed file, and what a Host makes of it on a given day.</summary>
public sealed class LicenceTests : IDisposable
{
    readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly Dictionary<string, byte[]> _trusted;
    static readonly DateOnly Agreed = new(2026, 10, 24);

    public LicenceTests()
    {
        var spki = _key.ExportSubjectPublicKeyInfo();
        _trusted = new Dictionary<string, byte[]> { [Licences.KeyId(spki)] = spki };
    }

    public void Dispose() => _key.Dispose();

    static LicenceTerms Standard(int computers = 30) => new()
    {
        Id = "CM-2026-0001", School = "Dausuva Gymnasium", SchoolCode = "190000000", CustomerId = "dausuva-gymnasium",
        Computers = computers, Agreement = Agreed, Ends = Licences.EndFor(Agreed), IssuedUtc = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc),
    };

    // ---- what may be issued ---------------------------------------------------------------------

    [Fact]
    public void A_licence_runs_one_year_from_the_day_of_agreement()
    {
        Assert.Equal(new DateOnly(2027, 10, 23), Licences.EndFor(new DateOnly(2026, 10, 24)));
        Assert.Equal(new DateOnly(2027, 12, 31), Licences.EndFor(new DateOnly(2027, 1, 1)));
        // Agreed on a leap day: the year ends on the last day of February, not the day before it.
        Assert.Equal(new DateOnly(2029, 2, 28), Licences.EndFor(new DateOnly(2028, 2, 29)));
        // Running over a leap day changes nothing: still the day before the anniversary.
        Assert.Equal(new DateOnly(2028, 2, 29), Licences.EndFor(new DateOnly(2027, 3, 1)));
        Assert.Equal(new DateOnly(2028, 2, 27), Licences.EndFor(new DateOnly(2027, 2, 28)));
    }

    [Theory]
    [InlineData(1, false, null)]
    [InlineData(49, false, null)]
    [InlineData(50, false, "separate agreement")]
    [InlineData(120, false, "separate agreement")]
    [InlineData(120, true, null)]
    [InlineData(0, false, "at least one computer")]
    public void A_standard_licence_covers_fewer_than_50_computers_and_more_need_a_separate_agreement(int computers, bool custom, string? problem)
    {
        var terms = Standard(computers) with { Custom = custom };
        if (problem is null) Assert.Null(Licences.Problem(terms));
        else Assert.Contains(problem, Licences.Problem(terms));
    }

    [Fact]
    public void A_standard_licence_must_run_exactly_a_year_but_a_separate_agreement_may_not()
    {
        var shorter = Standard() with { Ends = Agreed.AddMonths(6) };
        Assert.Contains("one year", Licences.Problem(shorter));
        Assert.Null(Licences.Problem(shorter with { Custom = true }));
        Assert.Contains("ends before", Licences.Problem(shorter with { Custom = true, Ends = Agreed.AddDays(-1) }));
        Assert.Contains("number", Licences.Problem(Standard() with { Id = " " }));
        Assert.Contains("school", Licences.Problem(Standard() with { School = "" }));
    }

    [Fact]
    public void Terms_that_may_not_be_issued_are_not_signed() =>
        Assert.Throws<ArgumentException>(() => Licences.Sign(Standard(60), _key));

    // ---- the signed file ----------------------------------------------------------------------

    [Fact]
    public void A_signed_licence_reads_back_with_every_term()
    {
        var check = Licences.Check(Licences.Sign(Standard(), _key), _trusted, Agreed);
        Assert.Equal(LicenceState.Active, check.State);
        Assert.True(check.Signed);
        Assert.Equal(Standard(), check.Terms);
        Assert.Equal(364, check.DaysLeft);
        Assert.Equal("", check.Problem);
    }

    [Fact]
    public void The_layout_of_the_file_does_not_matter_but_every_value_does()
    {
        var file = JsonNode.Parse(Licences.Sign(Standard(), _key))!.AsObject();
        // Written again without indentation and with the terms in another order: still the same licence.
        var body = file["licence"]!.AsObject();
        var reordered = new JsonObject(body.Reverse().Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())));
        file["licence"] = reordered;
        Assert.Equal(LicenceState.Active, Licences.Check(file.ToJsonString(), _trusted, Agreed).State);

        foreach (var (key, value) in new (string, JsonNode)[]
                 {
                     ("computers", 45), ("ends", "2030-10-23"), ("school", "Another School"), ("custom", true), ("customerId", "other"),
                 })
        {
            var changed = JsonNode.Parse(Licences.Sign(Standard(), _key))!.AsObject();
            changed["licence"]![key] = value;
            var check = Licences.Check(changed.ToJsonString(), _trusted, Agreed);
            Assert.Equal(LicenceState.Invalid, check.State);
            Assert.Contains("changed", check.Problem);
            Assert.False(check.KeyUnknown);
        }
    }

    [Fact]
    public void A_licence_signed_with_another_key_is_not_trusted()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var check = Licences.Check(Licences.Sign(Standard(), other), _trusted, Agreed);
        Assert.Equal(LicenceState.Invalid, check.State);
        Assert.Contains("key this program does not know", check.Problem);
        Assert.False(check.Signed);
        Assert.True(check.KeyUnknown);
        Assert.Equal(Standard(), check.Terms);
    }

    [Fact]
    public void A_signature_moved_onto_another_keys_id_fails()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = JsonNode.Parse(Licences.Sign(Standard(49), other))!.AsObject();
        forged["keyId"] = _trusted.Keys.Single();
        Assert.Equal(LicenceState.Invalid, Licences.Check(forged.ToJsonString(), _trusted, Agreed).State);
    }

    [Theory]
    [InlineData(null, LicenceState.Missing)]
    [InlineData("   ", LicenceState.Missing)]
    [InlineData("not json", LicenceState.Invalid)]
    [InlineData("[]", LicenceState.Invalid)]
    [InlineData("""{"kind":"something-else"}""", LicenceState.Invalid)]
    [InlineData("""{"kind":"classroom-manager-licence","licence":{"v":2}}""", LicenceState.Invalid)]
    [InlineData("""{"kind":"classroom-manager-licence","licence":{"v":1,"agreement":"soon"}}""", LicenceState.Invalid)]
    public void A_missing_or_damaged_file_is_said_to_be_so_and_never_throws(string? text, LicenceState state) =>
        Assert.Equal(state, Licences.Check(text, _trusted, Agreed).State);

    [Fact]
    public void A_damaged_signature_is_invalid()
    {
        var file = JsonNode.Parse(Licences.Sign(Standard(), _key))!.AsObject();
        file["signature"] = "@@not base64@@";
        Assert.Equal(LicenceState.Invalid, Licences.Check(file.ToJsonString(), _trusted, Agreed).State);
    }

    // ---- the state on a day -------------------------------------------------------------------

    [Theory]
    [InlineData("2026-10-24", LicenceState.Active, 364)]
    [InlineData("2027-09-22", LicenceState.Active, 31)]
    [InlineData("2027-09-23", LicenceState.EndingSoon, 30)]
    [InlineData("2027-10-23", LicenceState.EndingSoon, 0)]
    [InlineData("2027-10-24", LicenceState.Expired, 0)]
    [InlineData("2031-01-01", LicenceState.Expired, 0)]
    public void The_state_follows_the_last_day(string day, LicenceState state, int daysLeft)
    {
        var check = Licences.Check(Licences.Sign(Standard(), _key), _trusted, DateOnly.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal((state, daysLeft), (check.State, check.DaysLeft));
        // An ended licence is still a genuine one: the program keeps working and says it ended.
        Assert.True(check.Signed);
    }

    [Fact]
    public void The_key_id_is_the_start_of_the_keys_hash_and_the_build_trusts_only_listed_keys()
    {
        var spki = _key.ExportSubjectPublicKeyInfo();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(spki))[..16], Licences.KeyId(spki));
        Assert.Equal(LicenceKeys.Trusted.Length, Licences.TrustedKeys().Count);
    }
}
