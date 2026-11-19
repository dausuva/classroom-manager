// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public sealed class AppAccountsTests
{
    [Fact]
    public void Spotify_names_the_remembered_account()
    {
        const string prefs = "language=\"en\"\nautologin.canonical_username=\"student42\"\nautologin.username=\"student42\"\napp.window.position.x=10\n";
        Assert.Equal("student42", AppAccounts.SpotifyUser(prefs));
        Assert.Equal("", AppAccounts.SpotifyUser("language=\"en\"\n"));
    }

    [Fact]
    public void Steam_lists_each_remembered_account()
    {
        const string vdf = """
            "users"
            {
            	"76561198000000001"
            	{
            		"AccountName"		"gamer_one"
            		"PersonaName"		"One"
            		"MostRecent"		"1"
            	}
            	"76561198000000002"
            	{
            		"AccountName"		"gamer_two"
            		"PersonaName"		""
            		"MostRecent"		"0"
            	}
            }
            """;
        Assert.Equal([("gamer_one", "One", true), ("gamer_two", "", false)], AppAccounts.SteamUsers(vdf));
        Assert.Empty(AppAccounts.SteamUsers("not a vdf"));
    }

    [Fact]
    public void Dropbox_and_Teams_are_read_and_bad_files_give_nothing()
    {
        var info = """{"personal": {"path": "C:\\Users\\pc\\Dropbox", "host": 1, "is_team": false}, "business": {"path": "C:\\Users\\pc\\Dropbox (School)"}}""";
        Assert.Equal([("Personal account", @"C:\Users\pc\Dropbox"), ("Business account", @"C:\Users\pc\Dropbox (School)")], AppAccounts.DropboxAccounts(info));
        Assert.Empty(AppAccounts.DropboxAccounts("{broken"));

        Assert.Equal("pupil@school.lt", AppAccounts.TeamsUser("""{"upnWindowUserUpn":"pupil@school.lt","appPreferenceSettings":{}}"""));
        Assert.Equal("", AppAccounts.TeamsUser("[]"));
        Assert.Equal("", AppAccounts.TeamsUser("not json"));
    }

    [Fact]
    public void An_answer_survives_the_wire_and_reads_as_one_line()
    {
        var data = JsonNode.Parse(new JsonObject
        {
            [AppAccounts.Accounts] = new JsonArray(
                new AppAccounts.Account("OneDrive", "pupil@school.lt", "Work or school: School").ToJson(),
                new AppAccounts.Account("Discord", "", "In use").ToJson()),
        }.ToJsonString())!.AsObject();
        Assert.Equal(2, AppAccounts.From(data).Count);
        Assert.Equal("2 app account(s): OneDrive (pupil@school.lt), Discord.", AppAccounts.Describe(data));
        Assert.Equal("No app accounts found.", AppAccounts.Describe(new JsonObject()));
    }

    [Fact]
    public void The_list_runs_as_the_user_needs_an_admin_key_and_is_new_in_1_12()
    {
        Assert.True(Cmd.IsUserOnly(Cmd.AppAccountsList));
        Assert.Equal("1.12", Protocol.RequiredVersion(Cmd.AppAccountsList));
        Assert.Contains(Cmd.AppAccountsList, Protocol.Capabilities);
        Assert.Equal(OperatorRole.Admin, CommandAccess.RequiredRole(Cmd.AppAccountsList));
    }
}
