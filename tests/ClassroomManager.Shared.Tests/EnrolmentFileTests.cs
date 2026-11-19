// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class EnrolmentFileTests
{
    const string Key = "k7mq2-xh9vd-3ptwa-e8fzn-4bjrs";
    const string Sha = "B0F7B1FFD65A0E6E0112E4649BA823475C70E61C88EB328E46BBEA392F8FC28D";
    static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    static EnrolmentFile Make(string key = Key) =>
        EnrolmentFile.Create("410-17", "6532665a4a5e45f4bad18a2d66b44aaa", ["c4-65-16-bd-af-e1", "14:5A:FC:3C:D8:2F", "not a card"], Sha.ToLowerInvariant(), Now, key);

    [Fact]
    public void A_file_is_made_normalised_and_verifies_with_the_key_it_was_made_with()
    {
        var file = Make();

        Assert.Equal(["145AFC3CD82F", "C46516BDAFE1"], file.Cards);      // two cards, upper case, sorted; the junk entry is dropped
        Assert.Equal(Sha, file.CertificateSha256);
        Assert.Equal("2026-10-23T12:00:00Z", file.CreatedUtc);
        Assert.Equal(64, file.Tag.Length);
        Assert.True(file.Verify([Key]));
        Assert.True(file.Verify(["an old key", Key]));                  // the Host also tries its previous keys
        Assert.False(file.Verify(["a different key"]));
        Assert.False(file.Verify([]));
    }

    [Fact]
    public void Changing_any_field_breaks_the_tag()
    {
        foreach (var change in new Action<EnrolmentFile>[]
                 {
                     f => f.MachineName = "410-18",
                     f => f.ClientId = "other",
                     f => f.Cards = ["C46516BDB174"],
                     f => f.CertificateSha256 = new string('A', 64),
                     f => f.CreatedUtc = "2026-10-24T12:00:00Z",
                 })
        {
            var file = Make();
            change(file);
            Assert.False(file.Verify([Key]));
        }
    }

    [Fact]
    public void A_tag_that_is_not_hex_or_has_the_wrong_length_never_verifies_and_never_throws()
    {
        foreach (var tag in new[] { "", "zz", new string('0', 64), new string('G', 64), Make().Tag[..62] })
        {
            var file = Make();
            file.Tag = tag;
            Assert.False(file.Verify([Key]));
        }
    }

    [Fact]
    public void A_file_survives_the_trip_through_json()
    {
        var json = Make().ToJson();

        Assert.True(EnrolmentFile.TryParse(json, out var file, out var error), error);
        Assert.True(file.Verify([Key]));
        Assert.Equal("410-17", file.MachineName);
        Assert.Equal(Now, file.Created);
        Assert.Contains("\"certificateSha256\"", json);
        Assert.DoesNotContain(Key, json);                                  // the key itself is never in the file
    }

    [Fact]
    public void The_file_has_exactly_these_fields_and_no_others()
    {
        // The shape is a promise to every future reader. A new field is a decision: it must be covered by the tag.
        var names = System.Text.Json.Nodes.JsonNode.Parse(Make().ToJson())!.AsObject().Select(property => property.Key).Order().ToArray();

        Assert.Equal(["cards", "certificateSha256", "clientId", "createdUtc", "format", "machineName", "tag"], names);
    }

    [Fact]
    public void Files_that_are_not_enrolment_files_are_refused_with_a_reason()
    {
        Assert.False(EnrolmentFile.TryParse("not json", out _, out var e1));
        Assert.Contains("valid", e1);
        Assert.False(EnrolmentFile.TryParse("{\"format\":\"something-else\"}", out _, out var e2));
        Assert.Contains("format", e2, StringComparison.OrdinalIgnoreCase);

        var noFingerprint = Make();
        noFingerprint.CertificateSha256 = "ABC";
        Assert.False(EnrolmentFile.TryParse(noFingerprint.ToJson(), out _, out var e3));
        Assert.Contains("64", e3);

        var nobody = Make();
        nobody.Cards = [];
        nobody.ClientId = "";
        Assert.False(EnrolmentFile.TryParse(nobody.ToJson(), out _, out var e4));
        Assert.Contains("neither", e4);

        var undated = Make();
        undated.CreatedUtc = "yesterday";
        Assert.False(EnrolmentFile.TryParse(undated.ToJson(), out _, out var e5));
        Assert.Contains("date", e5);
    }
}
