// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Client;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-190: an access key that ends on a date, such as a substitute teacher's key that ends on Friday.</summary>
public sealed class KeyExpiryTests
{
    const string Primary = "synthetic-primary-key";
    const string Sub = "synthetic-substitute-key";

    sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    static ClientAccessKey Substitute(DateTime? notAfter) =>
        new() { Name = "Substitute", Role = "Teacher", Psk = Sub, NotAfterUtc = notAfter };

    [Fact]
    public void A_key_is_accepted_until_its_end_time_and_not_after()
    {
        var clock = new FakeClock();
        var keys = new KeyStore(Primary, [Substitute(clock.Now.UtcDateTime.AddDays(2))], clock);
        var substitute = keys.Named.Single();

        Assert.Equal(2, keys.Current.Count);
        Assert.True(keys.IsCurrent(substitute));

        clock.Advance(TimeSpan.FromDays(2) - TimeSpan.FromSeconds(1));
        Assert.Equal(2, keys.Current.Count);
        Assert.True(keys.IsCurrent(substitute));

        clock.Advance(TimeSpan.FromSeconds(1));                    // the end time itself
        Assert.Single(keys.Current);
        Assert.False(keys.IsCurrent(substitute));                  // a session that was open is refused at its next request
        Assert.Equal(2, keys.All.Count);                           // still stored, so the Host can see and remove it
        Assert.True(keys.IsCurrent(keys.Primary));
    }

    [Fact]
    public void The_labs_original_key_never_expires()
    {
        var clock = new FakeClock();
        var keys = new KeyStore(Primary, [], clock);

        clock.Advance(TimeSpan.FromDays(365 * 30));

        Assert.True(keys.IsCurrent(keys.Primary));
        Assert.Single(keys.Current);
    }

    [Fact]
    public void An_expiry_is_set_with_the_key_listed_and_saved_and_read_back_after_a_restart()
    {
        var clock = new FakeClock();
        var keys = new KeyStore(Primary, [], clock);
        List<ClientAccessKey>? saved = null;
        var handler = new KeyHandler(keys, _ => { }, list => saved = list.ToList(), clock);
        var end = clock.Now.AddDays(3).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        var response = handler.SetAccessKey(new JsonObject { ["name"] = "Substitute", ["role"] = "Teacher", ["key"] = Sub, ["expiresUtc"] = end },
            CallerIdentity.Service);

        Assert.True(response.Ok, response.Error);
        var listed = response.Data["keys"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "Substitute")!;
        Assert.Equal(end, listed["expiresUtc"]!.GetValue<string>());
        Assert.False(listed["expired"]!.GetValue<bool>());
        Assert.Null(response.Data["keys"]![0]!["expiresUtc"]);                          // the lab key shows none
        Assert.Equal(clock.Now.AddDays(3).UtcDateTime, saved!.Single().NotAfterUtc);

        var restarted = new KeyStore(Primary, saved, clock);
        clock.Advance(TimeSpan.FromDays(4));
        Assert.Single(restarted.Current);                                                // expired after the restart too
    }

    [Theory]
    [InlineData("not a date", "not a date and time")]
    [InlineData("2020-01-01T00:00:00Z", "already past")]
    [InlineData("2099-01-01T00:00:00Z", "more than ten years")]
    public void A_bad_expiry_is_refused_and_no_key_is_added(string expires, string reason)
    {
        var clock = new FakeClock();
        var keys = new KeyStore(Primary, [], clock);
        var handler = new KeyHandler(keys, _ => { }, _ => { }, clock);

        var response = handler.SetAccessKey(new JsonObject { ["name"] = "Substitute", ["role"] = "Teacher", ["key"] = Sub, ["expiresUtc"] = expires },
            CallerIdentity.Service);

        Assert.False(response.Ok);
        Assert.Contains(reason, response.Error);
        Assert.Empty(keys.Named);
    }

    [Fact]
    public void A_key_without_an_expiry_does_not_expire_and_the_observer_role_can_be_set()
    {
        var clock = new FakeClock();
        var keys = new KeyStore(Primary, [], clock);
        var handler = new KeyHandler(keys, _ => { }, _ => { }, clock);

        Assert.True(handler.SetAccessKey(new JsonObject { ["name"] = "Wall display", ["role"] = "Observer", ["key"] = Sub }, CallerIdentity.Service).Ok);
        clock.Advance(TimeSpan.FromDays(3000));

        var named = Assert.Single(keys.Current, key => key.Name == "Wall display");
        Assert.Equal(OperatorRole.Observer, named.Role);
        Assert.Null(named.NotAfter);
    }

    [Fact]
    public void Changing_only_the_expiry_makes_a_new_credential_so_sessions_of_the_old_one_end()
    {
        var clock = new FakeClock();
        var keys = new KeyStore(Primary, [Substitute(clock.Now.UtcDateTime.AddDays(1))], clock);
        var before = keys.Named.Single();
        var handler = new KeyHandler(keys, _ => { }, _ => { }, clock);

        handler.SetAccessKey(new JsonObject
        {
            ["name"] = "Substitute", ["role"] = "Teacher", ["key"] = Sub, ["expiresUtc"] = clock.Now.AddDays(5).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        }, CallerIdentity.Service);

        Assert.False(keys.IsCurrent(before));
        Assert.True(keys.IsCurrent(keys.Named.Single()));
    }

    [Fact]
    public void The_time_the_lab_key_was_changed_is_remembered_only_when_it_really_changes()
    {
        var clock = new FakeClock();
        var keys = new KeyStore(Primary, [], clock);
        Assert.Null(keys.PrimaryChanged);                         // unknown until the first change on this computer

        keys.Set(Primary);                                        // the same key again is not a change
        Assert.Null(keys.PrimaryChanged);

        clock.Advance(TimeSpan.FromDays(10));
        keys.Set("synthetic-replacement-key");
        Assert.Equal(clock.Now, keys.PrimaryChanged);

        var restarted = new KeyStore("synthetic-replacement-key", [], clock, primaryChanged: clock.Now.AddDays(-3));
        Assert.Equal(clock.Now.AddDays(-3), restarted.PrimaryChanged);
    }
}
