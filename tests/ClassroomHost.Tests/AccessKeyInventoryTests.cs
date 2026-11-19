// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class AccessKeyInventoryTests
{
    static readonly ClientInfo First = new() { Name = "PC-01", Address = "10.0.0.11" };
    static readonly ClientInfo Second = new() { Name = "PC-02", Address = "10.0.0.12" };
    static readonly ClientInfo Third = new() { Name = "PC-03", Address = "10.0.0.13" };

    static AccessKeyInfo Primary(string fingerprint = "aaaa1111") => new("Lab key", "Admin", fingerprint, true);

    [Fact]
    public void Client_response_is_read_without_any_secret()
    {
        var data = JsonNode.Parse("""
            { "keys": [
                { "name": "Lab key", "role": "Admin", "fingerprint": "aaaa1111", "primary": true },
                { "name": "Room 12 teacher", "role": "Teacher", "fingerprint": "bbbb2222", "primary": false }
            ] }
            """)!.AsObject();

        var keys = AccessKeyInventory.FromJson(data);

        Assert.Equal([Primary(), new AccessKeyInfo("Room 12 teacher", "Teacher", "bbbb2222", false)], keys);
        Assert.Empty(AccessKeyInventory.FromJson(new JsonObject()));
    }

    [Fact]
    public void Keys_are_merged_by_name_with_the_original_lab_key_first()
    {
        var teacher = new AccessKeyInfo("Room 12 teacher", "Teacher", "bbbb2222", false);
        var rows = AccessKeyInventory.Merge(new Dictionary<ClientInfo, IReadOnlyList<AccessKeyInfo>>
        {
            [First] = [Primary(), teacher],
            [Second] = [Primary(), teacher with { Name = "room 12 TEACHER" }],
            [Third] = [Primary()],
        });

        Assert.Equal(["Lab key", "Room 12 teacher"], rows.Select(row => row.Name));
        Assert.True(rows[0].Primary);
        Assert.Equal("3 of 3", rows[0].Computers);
        Assert.Contains("Rotate shared key", rows[0].Notes);

        Assert.Equal("Teacher", rows[1].Role);
        Assert.Equal("bbbb2222", rows[1].Fingerprint);
        Assert.Equal("2 of 3", rows[1].Computers);
        Assert.Equal("Missing on 1", rows[1].Notes);
        Assert.Equal([First, Second], rows[1].Clients);
    }

    [Fact]
    public void A_name_with_different_roles_or_secrets_between_computers_is_flagged()
    {
        var rows = AccessKeyInventory.Merge(new Dictionary<ClientInfo, IReadOnlyList<AccessKeyInfo>>
        {
            [First] = [new("Anna", "Teacher", "bbbb2222", false)],
            [Second] = [new("Anna", "Admin", "cccc3333", false)],
        });

        var row = Assert.Single(rows);
        Assert.Equal("Teacher / Admin", row.Role);
        Assert.Equal("(differs)", row.Fingerprint);
        Assert.Contains("Role differs between computers", row.Notes);
        Assert.Contains("Different keys share this name", row.Notes);
    }

    [Theory]
    [InlineData(new[] { "Teacher", "Teacher" }, true)]
    [InlineData(new[] { "teacher", "" }, true)]
    [InlineData(new[] { "Teacher", "Admin" }, false)]
    [InlineData(new[] { "Admin" }, false)]
    [InlineData(new[] { "", "" }, false)]
    [InlineData(new string[0], false)]
    public void Administrator_actions_are_hidden_only_when_every_known_role_is_teacher(string[] roles, bool teacherOnly)
    {
        Assert.Equal(teacherOnly, KeyRoleSummary.IsTeacherOnly(roles));
    }

    [Fact]
    public void No_answering_computers_gives_no_rows()
    {
        Assert.Empty(AccessKeyInventory.Merge(new Dictionary<ClientInfo, IReadOnlyList<AccessKeyInfo>>()));
    }

    [Fact]
    public void A_key_that_ends_is_read_with_its_end_shown_in_the_notes_and_called_ended_once_it_has()
    {
        var data = JsonNode.Parse("""
            { "keys": [
                { "name": "Lab key", "role": "Admin", "fingerprint": "aaaa1111", "primary": true },
                { "name": "Substitute", "role": "Teacher", "fingerprint": "bbbb2222", "primary": false, "expiresUtc": "2026-12-18T22:00:00Z", "expired": false },
                { "name": "Last term", "role": "Teacher", "fingerprint": "cccc3333", "primary": false, "expiresUtc": "2026-06-01T21:00:00Z", "expired": true }
            ] }
            """)!.AsObject();

        var keys = AccessKeyInventory.FromJson(data);
        var rows = AccessKeyInventory.Merge(new Dictionary<ClientInfo, IReadOnlyList<AccessKeyInfo>> { [First] = keys });

        Assert.Equal("2026-12-18T22:00:00Z", keys[1].Ends);
        Assert.Contains("Ends 2026-12-18 22:00:00 UTC", rows.Single(row => row.Name == "Substitute").Notes);
        Assert.Contains("Ended 2026-06-01 21:00:00 UTC", rows.Single(row => row.Name == "Last term").Notes);
        Assert.DoesNotContain("Ends", rows.Single(row => row.Name == "Lab key").Notes);
    }

    [Fact]
    public void An_end_that_differs_or_is_missing_on_some_computers_is_flagged()
    {
        var rows = AccessKeyInventory.Merge(new Dictionary<ClientInfo, IReadOnlyList<AccessKeyInfo>>
        {
            [First] = [new("Anna", "Teacher", "bbbb2222", false, "2026-12-18T22:00:00Z")],
            [Second] = [new("Anna", "Teacher", "bbbb2222", false, "")],
        });

        Assert.Contains("No end on some computers", rows.Single().Notes);
    }

    [Theory]
    [InlineData("", true, "")]
    [InlineData("   ", true, "")]
    [InlineData("2026-12-18", true, "end of that day")]
    [InlineData("not a date", false, "not a date")]
    [InlineData("2020-01-01", false, "already past")]
    public void The_end_date_is_read_as_the_end_of_that_day_on_this_computer_and_refused_when_it_is_not_a_date_or_past(string typed, bool ok, string expectation)
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Local);

        var accepted = AccessKeyInventory.TryEndOfDay(typed, now, out var utc, out var reason);

        Assert.Equal(ok, accepted);
        if (!ok) Assert.Contains(expectation, reason);
        if (ok && typed.Trim().Length == 0) Assert.Equal("", utc);
        if (ok && typed.Trim().Length > 0)
        {
            var parsed = DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            Assert.Equal(new DateTime(2026, 12, 19, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), parsed);
        }
    }
}
