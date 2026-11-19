// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClassroomManager.Shared;

/// <summary>
/// Accounts that apps on a student computer are signed in to (apps.accounts.list): OneDrive, Microsoft 365,
/// the accounts Windows keeps for its apps, Teams, Dropbox, Google Drive, Steam, Spotify, browser profiles
/// and a few apps that only show they are in use. Only what the apps keep in plain view is read: account
/// names, never passwords or sign-in tokens. An app whose account is stored encrypted, or only together
/// with its sign-in token, is reported as in use without a name.
/// </summary>
public static partial class AppAccounts
{
    public const string Accounts = "accounts";

    /// <summary>One app account: the app, the account name ("" when the app does not show it) and what else is known.</summary>
    public sealed record Account(string App, string Name, string Detail)
    {
        public JsonObject ToJson() => new() { ["app"] = App, ["name"] = Name, ["detail"] = Detail };

        public static Account FromJson(JsonNode? node) => new(
            node?["app"]?.GetValue<string>() ?? "", node?["name"]?.GetValue<string>() ?? "", node?["detail"]?.GetValue<string>() ?? "");
    }

    public static List<Account> From(JsonObject data) => (data[Accounts] as JsonArray ?? []).Select(Account.FromJson).ToList();

    public static string Describe(JsonObject data)
    {
        var accounts = From(data);
        return accounts.Count == 0
            ? "No app accounts found."
            : $"{accounts.Count} app account(s): {string.Join(", ", accounts.Select(account => account.Name.Length > 0 ? $"{account.App} ({account.Name})" : account.App))}.";
    }

    // ---- readers of what the apps keep, one per app; each takes the file text and never throws ---------

    /// <summary>Spotify's prefs file: the line autologin.username="..." names the remembered account.</summary>
    public static string SpotifyUser(string prefs)
    {
        var match = SpotifyLine().Match(prefs);
        return match.Success ? match.Groups[1].Value : "";
    }

    [GeneratedRegex(@"^autologin\.(?:canonical_)?username=""([^""]*)""", RegexOptions.Multiline)]
    private static partial Regex SpotifyLine();

    /// <summary>Steam's config\loginusers.vdf: each remembered account's sign-in name and the name others see.</summary>
    public static List<(string Account, string Persona, bool MostRecent)> SteamUsers(string vdf)
    {
        var users = new List<(string, string, bool)>();
        foreach (Match block in SteamBlock().Matches(vdf))
        {
            string Value(string name) => Regex.Match(block.Groups[1].Value, $"\"{name}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase) is { Success: true } found ? found.Groups[1].Value : "";
            if (Value("AccountName").Length > 0) users.Add((Value("AccountName"), Value("PersonaName"), Value("MostRecent") == "1"));
        }
        return users;
    }

    [GeneratedRegex(@"""\d{10,20}""\s*\{([^{}]*)\}")]
    private static partial Regex SteamBlock();

    /// <summary>Dropbox's info.json: a "personal" and/or "business" account with its folder. Dropbox does not keep the email there.</summary>
    public static List<(string Kind, string Folder)> DropboxAccounts(string infoJson)
    {
        var accounts = new List<(string, string)>();
        try
        {
            if (JsonNode.Parse(infoJson) is not JsonObject info) return accounts;
            foreach (var (kind, label) in new[] { ("personal", "Personal account"), ("business", "Business account") })
                if (info[kind] is JsonObject account)
                    accounts.Add((label, account["path"]?.GetValue<string>() ?? ""));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return accounts;
    }

    /// <summary>Classic Teams' desktop-config.json: the account it signs in with.</summary>
    public static string TeamsUser(string desktopConfig)
    {
        try
        {
            return JsonNode.Parse(desktopConfig)?["upnWindowUserUpn"]?.GetValue<string>() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return ""; }
    }
}
