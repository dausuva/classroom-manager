// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class LicenceCapacityTests
{
    static ClientInfo Pc(string name, bool online) => new() { Address = "10.42.0." + (1 + Math.Abs(name.GetHashCode() % 200)), Name = name, Status = online ? "Online" : "Offline (no reply)" };

    static LicenceCheck Signed(LicenceState state, int computers, bool custom = false) =>
        new(state, new LicenceTerms { Id = "CM-2026-0001", School = "Test School", Computers = computers, Custom = custom }, 100, "");

    [Fact]
    public void A_build_made_from_the_source_has_no_limit()
    {
        Assert.Null(LicenceCapacity.Allowed(officialBuild: false, buildHasKeys: true, new LicenceCheck(LicenceState.Missing, null, 0, "")));
    }

    [Fact]
    public void An_official_build_that_lists_no_licence_keys_cannot_judge_a_licence_and_has_no_limit()
    {
        Assert.Null(LicenceCapacity.Allowed(officialBuild: true, buildHasKeys: false, new LicenceCheck(LicenceState.Missing, null, 0, "")));
    }

    [Theory]
    [InlineData(LicenceState.Missing)]
    [InlineData(LicenceState.Invalid)]
    public void An_official_build_without_a_usable_licence_is_free_for_three_computers(LicenceState state)
    {
        Assert.Equal(3, LicenceCapacity.Allowed(true, true, new LicenceCheck(state, null, 0, "")));
    }

    [Fact]
    public void A_licence_from_an_issuer_this_build_does_not_know_is_not_held_against_the_school()
    {
        Assert.Null(LicenceCapacity.Allowed(true, true, new LicenceCheck(LicenceState.Invalid, null, 0, "") { KeyUnknown = true }));
    }

    [Theory]
    [InlineData(LicenceState.Active)]
    [InlineData(LicenceState.EndingSoon)]
    [InlineData(LicenceState.Expired)]
    public void A_licence_covers_the_computers_it_names_and_keeps_doing_so_after_it_ends(LicenceState state)
    {
        Assert.Equal(30, LicenceCapacity.Allowed(true, true, Signed(state, 30)));
    }

    [Fact]
    public void A_licence_for_fewer_than_three_never_lowers_the_free_tier_and_a_custom_agreement_has_no_limit()
    {
        Assert.Equal(3, LicenceCapacity.Allowed(true, true, Signed(LicenceState.Active, 2)));
        Assert.Null(LicenceCapacity.Allowed(true, true, Signed(LicenceState.Active, 80, custom: true)));
    }

    [Fact]
    public void The_first_three_online_computers_are_in_use_and_a_fourth_selected_one_waits()
    {
        var roster = new[] { Pc("A", true), Pc("B", true), Pc("C", true), Pc("D", true), Pc("E", true) };
        var plan = LicenceCapacity.Plan(roster, [roster[1], roster[3], roster[4]], 3);
        Assert.Equal(["B"], plan.Acting.Select(c => c.Name));
        Assert.Equal(["D", "E"], plan.Waiting.Select(c => c.Name));
    }

    [Fact]
    public void Offline_computers_do_not_use_up_the_allowance_and_go_along_with_the_action()
    {
        var roster = new[] { Pc("A", false), Pc("B", true), Pc("C", true), Pc("D", true), Pc("E", true) };
        var plan = LicenceCapacity.Plan(roster, roster, 3);
        Assert.Equal(["A", "B", "C", "D"], plan.Acting.Select(c => c.Name));
        Assert.Equal(["E"], plan.Waiting.Select(c => c.Name));
    }

    [Fact]
    public void When_every_selected_computer_waits_the_message_says_nothing_was_sent_and_how_to_add_a_licence()
    {
        var roster = new[] { Pc("A", true), Pc("B", true), Pc("C", true), Pc("D", true) };
        var plan = LicenceCapacity.Plan(roster, [roster[3]], 3);
        Assert.Empty(plan.Acting);
        var text = LicenceCapacity.Explain(3, plan);
        Assert.Contains("Nothing was sent", text);
        Assert.Contains("D", text);
        Assert.Contains("add your licence file", text);
    }

    [Fact]
    public void The_message_names_where_the_action_went_and_who_waits()
    {
        var roster = new[] { Pc("A", true), Pc("B", true), Pc("C", true), Pc("D", true) };
        var text = LicenceCapacity.Explain(3, LicenceCapacity.Plan(roster, roster, 3));
        Assert.Contains("Sent to: A, B, C.", text);
        Assert.Contains("Waiting for a licence: D.", text);
        Assert.Contains("Nothing already running", text);
    }
}

public sealed class LicenceCapacityCommandLineTests
{
    [Fact]
    public void Reading_status_waking_key_changes_and_client_updates_are_never_limited()
    {
        foreach (var command in new[] { "status", "wake", "derive-keys", "update" }) Assert.Contains(command, LicenceCapacity.CommandsNotLimited);
        foreach (var command in new[] { "lock", "message", "exam", "command", "install" }) Assert.DoesNotContain(command, LicenceCapacity.CommandsNotLimited);
    }

    [Fact]
    public async Task A_computer_is_online_when_something_listens_on_its_port_and_not_when_nothing_does()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try { Assert.True(await LicenceCapacity.IsOnlineAsync("127.0.0.1", port, TimeSpan.FromSeconds(2))); }
        finally { listener.Stop(); }
        Assert.False(await LicenceCapacity.IsOnlineAsync("127.0.0.1", port, TimeSpan.FromSeconds(2)));
    }
}

public sealed class LicenceFromTheDeskTests
{
    static (string File, Dictionary<string, byte[]> Keys) Issue(int computers, DateOnly agreement)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var terms = new LicenceTerms { Id = "CM-2026-9999", School = "Test School", Computers = computers, Agreement = agreement, Ends = Licences.EndFor(agreement), IssuedUtc = DateTime.UtcNow };
        return (Licences.Sign(terms, key), new Dictionary<string, byte[]> { [Licences.KeyId(publicKey)] = publicKey });
    }

    [Fact]
    public void A_licence_signed_by_the_desk_key_raises_the_free_tier_to_its_computers_and_a_forged_one_does_not()
    {
        var today = new DateOnly(2026, 11, 4);
        var (file, keys) = Issue(12, today);
        Assert.Equal(12, LicenceCapacity.Allowed(true, true, Licences.Check(file, keys, today)));

        var (_, otherKeys) = Issue(12, today);   // a different key: the file is not signed by anyone this build trusts
        var forged = Licences.Check(file, otherKeys, today);
        Assert.False(forged.Signed);
        Assert.Null(LicenceCapacity.Allowed(true, true, forged));   // unknown issuer: not held against the school
    }

    [Fact]
    public void The_licence_still_covers_its_computers_after_the_year_has_ended()
    {
        var (file, keys) = Issue(30, new DateOnly(2025, 1, 10));
        var check = Licences.Check(file, keys, new DateOnly(2026, 11, 4));
        Assert.Equal(LicenceState.Expired, check.State);
        Assert.Equal(30, LicenceCapacity.Allowed(true, true, check));
    }

    [Fact]
    public void The_build_trusts_the_desk_key()
    {
        Assert.Single(LicenceKeys.Trusted);
        Assert.Single(Licences.TrustedKeys());
    }
}

public sealed class CliLicenceLimitTests
{
    static ClientInfo Pc(string name) => new() { Address = "10.42.1." + name[^1], Name = name };

    [Fact]
    public async Task A_command_line_run_acts_on_the_first_three_that_answer_and_reports_the_rest_as_waiting_for_a_licence()
    {
        var clients = new List<ClientInfo> { Pc("PC-1"), Pc("PC-2"), Pc("PC-3"), Pc("PC-4"), Pc("PC-5") };
        var online = new HashSet<string> { "PC-1", "PC-2", "PC-4", "PC-5" };
        var waiting = await CliLicenceLimit.ApplyAsync(clients, "lock", 3, client => Task.FromResult(online.Contains(client.Name)));
        Assert.Equal(["PC-1", "PC-2", "PC-3", "PC-4"], clients.Select(c => c.Name));   // PC-3 is offline and goes along; PC-4 is the third online one
        var result = Assert.Single(waiting);
        Assert.Equal("PC-5", result.Computer);
        Assert.False(result.Ok);
        Assert.Contains("Waiting for a licence", result.Detail);
        Assert.Contains("up to 3 computers", result.Detail);
    }

    [Fact]
    public async Task Nothing_is_held_back_without_a_limit_or_for_a_command_that_is_never_limited()
    {
        var clients = new List<ClientInfo> { Pc("PC-1"), Pc("PC-2"), Pc("PC-3"), Pc("PC-4") };
        Assert.Empty(await CliLicenceLimit.ApplyAsync(clients, "lock", null, _ => Task.FromResult(true)));
        Assert.Empty(await CliLicenceLimit.ApplyAsync(clients, "status", 3, _ => Task.FromResult(true)));
        Assert.Equal(4, clients.Count);
    }
}
