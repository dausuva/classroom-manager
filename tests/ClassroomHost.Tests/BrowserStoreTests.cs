// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Client;
using ClassroomManager.Shared;
using Microsoft.Win32;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// The client's browser management against a scratch registry key and a scratch folder laid out
/// like a real profile, so nothing of the person running the tests is read or changed.
/// </summary>
public sealed class BrowserStoreTests : IDisposable
{
    readonly string _id = "ClassroomManagerTests-" + Guid.NewGuid().ToString("N")[..8];
    readonly string _folder;
    readonly RegistryKey _root;

    public BrowserStoreTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), _id);
        _root = Registry.CurrentUser.CreateSubKey($@"Software\{_id}");
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\{_id}", throwOnMissingSubKey: false);
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    // ---- policies ------------------------------------------------------------------------------------

    BrowserPolicyStore Policies() => new(_root, Path.Combine(_folder, "state", "browser-policies.json"));

    [Fact]
    public void Secure_DNS_is_turned_off_in_all_three_browsers_and_put_back_as_it_was()
    {
        // Edge had it forced on by someone else; Chrome and Firefox had no setting.
        using (var edge = _root.CreateSubKey(EdgeKey)) edge.SetValue("DnsOverHttpsMode", "secure", RegistryValueKind.String);
        var store = Policies();
        Assert.Equal("off", store.State(BrowserRules.NoSecureDns));

        store.Set(BrowserRules.NoSecureDns, true);
        store.Set(BrowserRules.NoSecureDns, true);   // every change to the block list asks again

        Assert.Equal("on", store.State(BrowserRules.NoSecureDns));
        using (var chrome = _root.OpenSubKey(ChromeKey)) Assert.Equal("off", chrome!.GetValue("DnsOverHttpsMode"));
        using (var edge = _root.OpenSubKey(EdgeKey)) Assert.Equal("off", edge!.GetValue("DnsOverHttpsMode"));
        using (var firefox = _root.OpenSubKey(FirefoxKey + @"\DNSOverHTTPS"))
        {
            Assert.Equal(0, firefox!.GetValue("Enabled"));
            Assert.Equal(1, firefox.GetValue("Locked"));
        }

        store.Set(BrowserRules.NoSecureDns, false);

        using (var chrome = _root.OpenSubKey(ChromeKey)) Assert.Null(chrome?.GetValue("DnsOverHttpsMode"));
        using (var edge = _root.OpenSubKey(EdgeKey)) Assert.Equal("secure", edge!.GetValue("DnsOverHttpsMode"));
        using (var firefox = _root.OpenSubKey(FirefoxKey + @"\DNSOverHTTPS")) Assert.Null(firefox?.GetValue("Enabled"));
        // It is the Client's own measure, not one of the policies an administrator switches in Browsers...
        Assert.DoesNotContain(BrowserRules.NoSecureDns, BrowserRules.Policies);
    }

    const string ChromeKey = @"SOFTWARE\Policies\Google\Chrome";
    const string EdgeKey = @"SOFTWARE\Policies\Microsoft\Edge";
    const string FirefoxKey = @"SOFTWARE\Policies\Mozilla\Firefox";

    object? Value(string key, string name)
    {
        using var opened = _root.OpenSubKey(key);
        return opened?.GetValue(name);
    }

    [Fact]
    public void Preventing_profiles_writes_each_browsers_own_policy_and_turning_it_off_leaves_nothing_behind()
    {
        var store = Policies();
        Assert.Equal("off", store.State(BrowserRules.PreventProfiles));

        store.Set(BrowserRules.PreventProfiles, true);

        Assert.Equal("on", store.State(BrowserRules.PreventProfiles));
        Assert.Equal(0, Value(ChromeKey, "BrowserAddPersonEnabled"));
        Assert.Equal(0, Value(ChromeKey, "BrowserGuestModeEnabled"));
        Assert.Equal(0, Value(EdgeKey, "BrowserAddProfileEnabled"));
        Assert.Equal(0, Value(EdgeKey, "BrowserGuestModeEnabled"));
        Assert.Equal(1, Value(FirefoxKey, "BlockAboutProfiles"));
        Assert.Equal("off", store.State(BrowserRules.ClearOnExit));   // the other policy is untouched

        store.Set(BrowserRules.PreventProfiles, false);

        Assert.Equal("off", store.State(BrowserRules.PreventProfiles));
        Assert.Null(Value(ChromeKey, "BrowserAddPersonEnabled"));
        Assert.Null(Value(FirefoxKey, "BlockAboutProfiles"));
        Assert.False(File.Exists(Path.Combine(_folder, "state", "browser-policies.json")));
    }

    [Fact]
    public void A_value_someone_else_had_set_is_put_back_even_after_turning_the_policy_on_twice()
    {
        using (var edge = _root.CreateSubKey(EdgeKey)) edge.SetValue("BrowserGuestModeEnabled", 1, RegistryValueKind.DWord);
        var store = Policies();
        Assert.Equal("off", store.State(BrowserRules.PreventProfiles));

        store.Set(BrowserRules.PreventProfiles, true);
        Policies().Set(BrowserRules.PreventProfiles, true);   // again, as after a restart of the service
        Assert.Equal(0, Value(EdgeKey, "BrowserGuestModeEnabled"));

        Policies().Set(BrowserRules.PreventProfiles, false);

        Assert.Equal(1, Value(EdgeKey, "BrowserGuestModeEnabled"));
        Assert.Null(Value(EdgeKey, "BrowserAddProfileEnabled"));
    }

    [Fact]
    public void Clearing_on_close_includes_Chromes_list_and_sync_switch_and_is_partly_on_when_only_some_values_are_set()
    {
        var store = Policies();
        using (var firefox = _root.CreateSubKey(FirefoxKey)) firefox.SetValue("SanitizeOnShutdown", 1, RegistryValueKind.DWord);
        Assert.Equal("partly", store.State(BrowserRules.ClearOnExit));

        store.Set(BrowserRules.ClearOnExit, true);

        Assert.Equal("on", store.State(BrowserRules.ClearOnExit));
        Assert.Equal(1, Value(ChromeKey, "SyncDisabled"));
        Assert.Equal(1, Value(EdgeKey, "ClearBrowsingDataOnExit"));
        using (var list = _root.OpenSubKey(ChromeKey + @"\ClearBrowsingDataOnExitList"))
        {
            Assert.NotNull(list);
            Assert.Equal("browsing_history", list!.GetValue("1"));
            Assert.Contains("cookies_and_other_site_data", list.GetValueNames().Select(name => list.GetValue(name)));
        }

        store.Set(BrowserRules.ClearOnExit, false);

        Assert.Null(_root.OpenSubKey(ChromeKey + @"\ClearBrowsingDataOnExitList"));
        Assert.Null(Value(ChromeKey, "SyncDisabled"));
        Assert.Equal(1, Value(FirefoxKey, "SanitizeOnShutdown"));   // it was there before, so it stays
    }

    [Fact]
    public void Turning_off_a_policy_that_was_never_turned_on_here_changes_nothing()
    {
        using (var chrome = _root.CreateSubKey(ChromeKey)) chrome.SetValue("BrowserAddPersonEnabled", 0, RegistryValueKind.DWord);

        Policies().Set(BrowserRules.PreventProfiles, false);

        Assert.Equal(0, Value(ChromeKey, "BrowserAddPersonEnabled"));
    }

    // ---- browser data --------------------------------------------------------------------------------

    string Local => Path.Combine(_folder, "Local");
    string Roaming => Path.Combine(_folder, "Roaming");
    string Chrome => Path.Combine(Local, "Google", "Chrome", "User Data");
    string Edge => Path.Combine(Local, "Microsoft", "Edge", "User Data");
    string Firefox => Path.Combine(Roaming, "Mozilla", "Firefox");

    void Touch(string path, string content = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    BrowserDataStore Layout()
    {
        foreach (var profile in new[] { "Default", "Profile 1" })
        {
            Touch(Path.Combine(Chrome, profile, "History"));
            Touch(Path.Combine(Chrome, profile, "Bookmarks"));
            Touch(Path.Combine(Chrome, profile, "Login Data"));
            Touch(Path.Combine(Chrome, profile, "Network", "Cookies"));
            Touch(Path.Combine(Chrome, profile, "Cache", "Cache_Data", "f_000001"));
            Touch(Path.Combine(Chrome, profile, "Sessions", "Session_1"));
        }
        Touch(Path.Combine(Chrome, "Local State"), """
            { "profile": { "info_cache": { "Default": { "name": "Mokinys", "user_name": "" },
                                           "Profile 1": { "name": "Games", "user_name": "someone@example.com" } },
                           "profiles_order": ["Default", "Profile 1"], "last_used": "Profile 1" },
              "other": { "kept": true } }
            """);
        Touch(Path.Combine(Chrome, "Crashpad", "settings.dat"));          // not a profile folder
        Touch(Path.Combine(Edge, "Default", "History"));

        Touch(Path.Combine(Firefox, "profiles.ini"), string.Join("\n",
            "[Install308046B0AF4A39CB]", "Default=Profiles/abcd.default-release", "Locked=1", "",
            "[Profile0]", "Name=old", "IsRelative=1", "Path=Profiles/zzzz.default", "Default=1", "",
            "[Profile1]", "Name=default-release", "IsRelative=1", "Path=Profiles/abcd.default-release", "",
            "[Profile2]", "Name=elsewhere", "IsRelative=0", "Path=D:\\Portable\\profile", "",
            "[General]", "StartWithLastProfile=1", "Version=2"));
        foreach (var profile in new[] { "zzzz.default", "abcd.default-release" })
        {
            Touch(Path.Combine(Firefox, "Profiles", profile, "places.sqlite"));
            Touch(Path.Combine(Firefox, "Profiles", profile, "cookies.sqlite"));
            Touch(Path.Combine(Firefox, "Profiles", profile, "logins.json"));
            Touch(Path.Combine(Firefox, "Profiles", profile, "bookmarkbackups", "bookmarks-2026-10-13.jsonlz4"));
            Touch(Path.Combine(Local, "Mozilla", "Firefox", "Profiles", profile, "cache2", "entries", "A1"));
        }
        return new BrowserDataStore(Local, Roaming);
    }

    [Fact]
    public void Profiles_are_listed_with_the_browsers_own_names_and_each_browsers_first_one_is_marked()
    {
        var profiles = Layout().Profiles();

        Assert.Equal(
        [
            ("Chrome", "Default", "Mokinys", "", true), ("Chrome", "Profile 1", "Games", "someone@example.com", false),
            ("Edge", "Default", "Default", "", true),
            ("Firefox", "Profiles/zzzz.default", "old", "", false),
            // Firefox starts with the profile its installation points at, not the one marked Default=1 long ago.
            ("Firefox", "Profiles/abcd.default-release", "default-release", "", true),
            ("Firefox", "D:\\Portable\\profile", "elsewhere", "", false),
        ], profiles.Select(profile => (profile.Browser, profile.Id, profile.Name, profile.Account, profile.IsFirst)));
        Assert.Equal("Chrome \"Games\" (someone@example.com)", profiles[1].ToString());
    }

    [Fact]
    public void Clearing_history_and_cache_keeps_bookmarks_passwords_and_cookies()
    {
        var store = Layout();

        var cleared = store.Clear(history: true, cookies: false, cache: true);

        Assert.Equal(0, cleared.Failed);
        Assert.True(cleared.Files >= 11 && cleared.Bytes > 0);
        foreach (var profile in new[] { "Default", "Profile 1" })
        {
            Assert.False(File.Exists(Path.Combine(Chrome, profile, "History")));
            Assert.False(Directory.Exists(Path.Combine(Chrome, profile, "Cache")));
            Assert.False(Directory.Exists(Path.Combine(Chrome, profile, "Sessions")));
            Assert.True(File.Exists(Path.Combine(Chrome, profile, "Bookmarks")));
            Assert.True(File.Exists(Path.Combine(Chrome, profile, "Login Data")));
            Assert.True(File.Exists(Path.Combine(Chrome, profile, "Network", "Cookies")));
        }
        Assert.False(File.Exists(Path.Combine(Edge, "Default", "History")));
        Assert.True(File.Exists(Path.Combine(Chrome, "Crashpad", "settings.dat")));
        Assert.False(File.Exists(Path.Combine(Firefox, "Profiles", "abcd.default-release", "places.sqlite")));
        Assert.True(File.Exists(Path.Combine(Firefox, "Profiles", "abcd.default-release", "cookies.sqlite")));
        Assert.True(File.Exists(Path.Combine(Firefox, "Profiles", "abcd.default-release", "logins.json")));
        // Firefox rebuilds its bookmarks from this backup when places.sqlite is gone.
        Assert.True(File.Exists(Path.Combine(Firefox, "Profiles", "abcd.default-release", "bookmarkbackups", "bookmarks-2026-10-13.jsonlz4")));
        Assert.False(Directory.Exists(Path.Combine(Local, "Mozilla", "Firefox", "Profiles", "abcd.default-release", "cache2")));

        Assert.Equal(new BrowserDataStore.Cleared(0, 0, 0), store.Clear(history: true, cookies: false, cache: true));   // nothing left to do
    }

    [Fact]
    public void Cookies_are_only_cleared_when_asked_and_a_file_in_use_is_counted_not_fatal()
    {
        var store = Layout();
        using (File.Open(Path.Combine(Chrome, "Default", "Network", "Cookies"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var cleared = store.Clear(history: false, cookies: true, cache: false);

            Assert.Equal(1, cleared.Failed);
            Assert.True(File.Exists(Path.Combine(Chrome, "Default", "Network", "Cookies")));
            Assert.False(File.Exists(Path.Combine(Chrome, "Profile 1", "Network", "Cookies")));
            Assert.False(File.Exists(Path.Combine(Firefox, "Profiles", "abcd.default-release", "cookies.sqlite")));
            Assert.True(File.Exists(Path.Combine(Chrome, "Default", "History")));
        }
    }

    [Fact]
    public void Only_the_named_profiles_are_removed_and_the_others_stay()
    {
        var store = Layout();
        var firefoxOld = store.Profiles().Single(profile => profile.Browser == "Firefox" && profile.Name == "old");

        var removed = store.RemoveExtraProfiles(new HashSet<string> { firefoxOld.Key });

        Assert.Equal([firefoxOld.Key], removed.Select(profile => profile.Key));
        Assert.False(Directory.Exists(Path.Combine(Firefox, "Profiles", "zzzz.default")));
        Assert.True(Directory.Exists(Path.Combine(Chrome, "Profile 1")));   // an extra profile nobody named
    }

    [Fact]
    public void A_first_profile_is_never_removed_even_when_it_is_named()
    {
        var store = Layout();
        var first = store.Profiles().Where(profile => profile.IsFirst).Select(profile => profile.Key).ToHashSet();

        Assert.NotEmpty(first);
        Assert.Empty(store.RemoveExtraProfiles(first));
        Assert.True(Directory.Exists(Path.Combine(Chrome, "Default")));
        Assert.True(Directory.Exists(Path.Combine(Firefox, "Profiles", "abcd.default-release")));
    }

    [Fact]
    public void An_empty_selection_removes_nothing()
    {
        var store = Layout();

        Assert.Empty(store.RemoveExtraProfiles(new HashSet<string>()));
        Assert.True(Directory.Exists(Path.Combine(Chrome, "Profile 1")));
    }

    [Fact]
    public void A_request_with_a_list_names_profiles_and_one_without_means_all_extra_ones()
    {
        var chrome = new BrowserRules.Profile("Chrome", "Profile 1", "Games", "", false);

        Assert.Equal("Chrome|Profile 1", chrome.Key);
        Assert.True(BrowserRules.CanRemoveSelected("1.16"));
        Assert.False(BrowserRules.CanRemoveSelected("1.15"));
        Assert.False(BrowserRules.CanRemoveSelected(null));
    }

    [Fact]
    public void Removing_extra_profiles_keeps_each_browsers_first_one_and_tidies_the_browsers_own_lists()
    {
        var store = Layout();

        var removed = store.RemoveExtraProfiles();

        Assert.Equal([("Chrome", "Games"), ("Firefox", "old")], removed.Select(profile => (profile.Browser, profile.Name)));
        Assert.True(Directory.Exists(Path.Combine(Chrome, "Default")));
        Assert.False(Directory.Exists(Path.Combine(Chrome, "Profile 1")));
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Chrome, "Local State")))!;
        Assert.Null(state["profile"]!["info_cache"]!["Profile 1"]);
        Assert.NotNull(state["profile"]!["info_cache"]!["Default"]);
        Assert.Equal("Default", state["profile"]!["last_used"]!.GetValue<string>());
        Assert.Single(state["profile"]!["profiles_order"]!.AsArray());
        Assert.True(state["other"]!["kept"]!.GetValue<bool>());

        Assert.False(Directory.Exists(Path.Combine(Firefox, "Profiles", "zzzz.default")));
        Assert.False(Directory.Exists(Path.Combine(Local, "Mozilla", "Firefox", "Profiles", "zzzz.default")));
        Assert.True(Directory.Exists(Path.Combine(Firefox, "Profiles", "abcd.default-release")));
        var ini = File.ReadAllLines(Path.Combine(Firefox, "profiles.ini"));
        // The sections are numbered again without a gap, and the profile kept outside the folder is still listed.
        Assert.Equal(["[Install308046B0AF4A39CB]", "[Profile0]", "[Profile1]", "[General]"], ini.Where(line => line.StartsWith('[')));
        Assert.Contains("Path=Profiles/abcd.default-release", ini);
        Assert.Contains("Path=D:\\Portable\\profile", ini);
        Assert.DoesNotContain("Name=old", ini);

        Assert.DoesNotContain(store.RemoveExtraProfiles(), profile => profile.Browser == "Chrome");   // a second run finds none
    }

    [Fact]
    public void A_computer_without_any_browser_data_is_not_an_error()
    {
        var store = new BrowserDataStore(Path.Combine(_folder, "nothing"), Path.Combine(_folder, "nothing"));

        Assert.Empty(store.Profiles());
        Assert.Equal(new BrowserDataStore.Cleared(0, 0, 0), store.Clear(true, true, true));
        Assert.Empty(store.RemoveExtraProfiles());
    }
}
