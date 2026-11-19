// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// Runs in the user helper: the accounts that apps of the signed-in user are signed in to (see
/// <see cref="AppAccounts"/>). Each app is read on its own, so one that cannot be read does not hide the rest.
/// </summary>
sealed class AppAccountsHandler
{
    public Response List()
    {
        var found = new List<AppAccounts.Account>();
        foreach (var read in new Func<IEnumerable<AppAccounts.Account>>[]
                 {
                     OneDrive, Office, WindowsApps, WorkOrSchool, Teams, Dropbox, GoogleDrive, Steam, Spotify, BrowserProfiles, InUseOnly,
                 })
        {
            try { found.AddRange(read()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException) { }
        }
        var unique = found.DistinctBy(account => (account.App.ToLowerInvariant(), account.Name.ToLowerInvariant())).ToList();
        return Response.Success(new JsonObject
        {
            [AppAccounts.Accounts] = new JsonArray(unique.Select(account => (JsonNode)account.ToJson()).ToArray()),
        });
    }

    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Every subkey of a key below HKEY_CURRENT_USER, opened.</summary>
    static IEnumerable<RegistryKey> Subkeys(string path)
    {
        using var parent = Registry.CurrentUser.OpenSubKey(path);
        if (parent is null) yield break;
        foreach (var name in parent.GetSubKeyNames())
        {
            using var child = parent.OpenSubKey(name);
            if (child is not null) yield return child;
        }
    }

    static string Text(RegistryKey key, string name) => key.GetValue(name) as string ?? "";

    static IEnumerable<AppAccounts.Account> OneDrive()
    {
        foreach (var account in Subkeys(@"Software\Microsoft\OneDrive\Accounts"))
        {
            var email = Text(account, "UserEmail");
            if (email.Length == 0) continue;
            var personal = Path.GetFileName(account.Name).Equals("Personal", StringComparison.OrdinalIgnoreCase);
            var kind = personal ? "Personal" : $"Work or school: {Text(account, "DisplayName")}".TrimEnd(' ', ':');
            var folder = Text(account, "UserFolder");
            yield return new("OneDrive", email, folder.Length > 0 ? $"{kind}; {folder}" : kind);
        }
    }

    static IEnumerable<AppAccounts.Account> Office()
    {
        foreach (var identity in Subkeys(@"Software\Microsoft\Office\16.0\Common\Identity\Identities"))
        {
            var email = Text(identity, "EmailAddress");
            if (email.Length == 0) continue;
            var provider = Text(identity, "ProviderId");
            var kind = provider.Equals("liveId", StringComparison.OrdinalIgnoreCase) ? "Microsoft account" : "Work or school account";
            var name = Text(identity, "FriendlyName");
            yield return new("Microsoft 365 (Office)", email, name.Length > 0 ? $"{kind}; {name}" : kind);
        }
    }

    /// <summary>Microsoft accounts the user added to Windows for its apps (Microsoft Store, Mail, Xbox...).</summary>
    static IEnumerable<AppAccounts.Account> WindowsApps()
    {
        using var stored = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\IdentityCRL\StoredIdentities");
        foreach (var name in stored?.GetSubKeyNames() ?? [])
            if (name.Contains('@')) yield return new("Windows (Microsoft account for apps)", name, "");
    }

    static IEnumerable<AppAccounts.Account> WorkOrSchool()
    {
        foreach (var join in Subkeys(@"Software\Microsoft\Windows NT\CurrentVersion\WorkplaceJoin\JoinInfo"))
            if (Text(join, "UserEmail") is { Length: > 0 } email)
                yield return new("Windows (work or school account)", email, "");
    }

    static IEnumerable<AppAccounts.Account> Teams()
    {
        var config = Path.Combine(Roaming, "Microsoft", "Teams", "desktop-config.json");
        if (File.Exists(config) && AppAccounts.TeamsUser(File.ReadAllText(config)) is { Length: > 0 } user)
            yield return new("Microsoft Teams (classic)", user, "");
    }

    static IEnumerable<AppAccounts.Account> Dropbox()
    {
        foreach (var info in new[] { Path.Combine(Local, "Dropbox", "info.json"), Path.Combine(Roaming, "Dropbox", "info.json") }.Where(File.Exists))
        {
            foreach (var (kind, folder) in AppAccounts.DropboxAccounts(File.ReadAllText(info)))
                yield return new("Dropbox", "", folder.Length > 0 ? $"{kind}; {folder}" : kind);
            yield break;
        }
    }

    /// <summary>Google Drive for desktop keeps one folder per signed-in account, named by a number, not the email.</summary>
    static IEnumerable<AppAccounts.Account> GoogleDrive()
    {
        var drive = Path.Combine(Local, "Google", "DriveFS");
        if (!Directory.Exists(drive)) yield break;
        var accounts = Directory.EnumerateDirectories(drive).Count(folder => Path.GetFileName(folder).All(char.IsDigit));
        if (accounts > 0) yield return new("Google Drive", "", $"{accounts} account(s) signed in; Google Drive does not keep the email where it can be read");
    }

    static IEnumerable<AppAccounts.Account> Steam()
    {
        using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (steam is null) yield break;
        var users = Text(steam, "SteamPath") is { Length: > 0 } path && File.Exists(Path.Combine(path, "config", "loginusers.vdf"))
            ? AppAccounts.SteamUsers(File.ReadAllText(Path.Combine(path, "config", "loginusers.vdf")))
            : [];
        foreach (var (account, persona, recent) in users)
            yield return new("Steam", account, (persona.Length > 0, recent) switch
            {
                (true, true) => $"Shown as {persona}; used last",
                (true, false) => $"Shown as {persona}",
                (false, true) => "Used last",
                _ => "",
            });
        if (users.Count == 0 && Text(steam, "AutoLoginUser") is { Length: > 0 } auto)
            yield return new("Steam", auto, "Signs in automatically");
    }

    static IEnumerable<AppAccounts.Account> Spotify()
    {
        var prefs = Path.Combine(Roaming, "Spotify", "prefs");
        if (File.Exists(prefs) && AppAccounts.SpotifyUser(File.ReadAllText(prefs)) is { Length: > 0 } user)
            yield return new("Spotify", user, "");
    }

    static IEnumerable<AppAccounts.Account> BrowserProfiles() =>
        new BrowserDataStore(Local, Roaming).Profiles()
            .Where(profile => profile.Account.Length > 0)
            .Select(profile => new AppAccounts.Account($"{profile.Browser} profile \"{profile.Name}\"", profile.Account, ""));

    /// <summary>Apps whose account is stored encrypted or only with its sign-in token: they show that they are in use, no more.</summary>
    static IEnumerable<AppAccounts.Account> InUseOnly()
    {
        var places = new (string App, string Path)[]
        {
            ("Discord", Path.Combine(Roaming, "discord", "Local Storage")),
            ("Epic Games", Path.Combine(Local, "EpicGamesLauncher", "Saved")),
            ("Telegram", Path.Combine(Roaming, "Telegram Desktop", "tdata")),
            ("iCloud Drive", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "iCloudDrive")),
        };
        foreach (var (app, path) in places)
            if (Directory.Exists(path)) yield return new(app, "", "In use; the account is not stored where it can be read");
    }
}
