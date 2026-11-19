// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;

namespace ClassroomManager.Shared;

/// <summary>
/// What Classroom Manager can do to the browsers on a student computer, shared by the Host's dialog
/// and the client's handlers.
///
/// Two machine-wide policies, written where each browser's own administrative policies live, so
/// the browser enforces them itself and shows them on its "managed by your organization" page:
/// no new profiles or guest windows, and browsing data cleared whenever the browser closes.
/// Turning one off puts back exactly what was there before.
///
/// Two actions on the signed-in user's own browser data, which close the browsers first because
/// the files are in use while they run: clearing history, cookies and cache now, and removing
/// every profile except each browser's first one.
/// </summary>
public static class BrowserRules
{
    // ---- policies (browser.policy.get / browser.policy.set) ---------------------------------------

    public const string PreventProfiles = "preventProfiles";

    /// <summary>
    /// browser.policy.get/set argument (client protocol 1.13): true acts on the signed-in user's own policies
    /// instead of the computer's. Chrome, Edge and Firefox read both; the computer's win where both are set.
    /// </summary>
    public const string ForUser = "forUser";
    public const string ClearOnExit = "clearOnExit";

    /// <summary>Page translation turned off, one policy per browser so each can be chosen on its own (client protocol 1.12).</summary>
    public const string NoTranslateChrome = "noTranslateChrome";
    public const string NoTranslateEdge = "noTranslateEdge";
    public const string NoTranslateFirefox = "noTranslateFirefox";
    public static readonly string[] TranslationPolicies = [NoTranslateChrome, NoTranslateEdge, NoTranslateFirefox];

    /// <summary>
    /// Firefox has no policy of its own for translation; its Preferences policy (JSON in one registry value)
    /// locks the setting off. While this is on, other preferences an administrator set through that value
    /// are replaced; turning it off puts them back.
    /// </summary>
    const string FirefoxNoTranslate = """{"browser.translations.enable":{"Value":false,"Status":"locked"}}""";

    /// <summary>One registry value of a policy. A <see cref="List"/> is stored as a subkey with numbered values.</summary>
    public sealed record Setting(string Browser, string Key, string Name, int Number = 0, string[]? List = null, string? Text = null);

    /// <summary>
    /// Not one of the policies an administrator switches: the Client keeps it on by itself for as long
    /// as any website is blocked. A browser using secure DNS (DNS over HTTPS) asks a server on the
    /// internet where a website is and never looks at the hosts file, so the block would not apply to it.
    /// </summary>
    public const string NoSecureDns = "noSecureDns";

    const string Chrome = @"SOFTWARE\Policies\Google\Chrome";
    const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";
    const string Firefox = @"SOFTWARE\Policies\Mozilla\Firefox";

    /// <summary>The registry values that make up a policy; an unknown name has none.</summary>
    public static IReadOnlyList<Setting> Settings(string policy) => policy switch
    {
        PreventProfiles =>
        [
            new("Chrome", Chrome, "BrowserAddPersonEnabled", 0),
            new("Chrome", Chrome, "BrowserGuestModeEnabled", 0),
            new("Edge", Edge, "BrowserAddProfileEnabled", 0),
            new("Edge", Edge, "BrowserGuestModeEnabled", 0),
            // Firefox has no "no new profiles" policy; closing its profile page is what it offers.
            new("Firefox", Firefox, "BlockAboutProfiles", 1),
        ],
        ClearOnExit =>
        [
            // Chrome only clears on exit while sync is off, so that comes with it.
            new("Chrome", Chrome, "SyncDisabled", 1),
            new("Chrome", Chrome, "ClearBrowsingDataOnExitList", List:
                ["browsing_history", "download_history", "cookies_and_other_site_data", "cached_images_and_files", "autofill", "hosted_app_data"]),
            new("Edge", Edge, "ClearBrowsingDataOnExit", 1),
            new("Firefox", Firefox, "SanitizeOnShutdown", 1),
        ],
        NoTranslateChrome => [new("Chrome", Chrome, "TranslateEnabled", 0)],
        NoTranslateEdge => [new("Edge", Edge, "TranslateEnabled", 0)],
        NoTranslateFirefox => [new("Firefox", Firefox, "Preferences", Text: FirefoxNoTranslate)],
        NoSecureDns =>
        [
            new("Chrome", Chrome, "DnsOverHttpsMode", Text: "off"),
            new("Edge", Edge, "DnsOverHttpsMode", Text: "off"),
            new("Firefox", Firefox + @"\DNSOverHTTPS", "Enabled", 0),
            new("Firefox", Firefox + @"\DNSOverHTTPS", "Locked", 1),
        ],
        _ => [],
    };

    public static readonly string[] Policies = [PreventProfiles, ClearOnExit, .. TranslationPolicies];

    public static string PolicyName(string policy) => policy switch
    {
        PreventProfiles => "No new browser profiles or guest windows",
        ClearOnExit => "Clear browsing data when the browser closes",
        NoTranslateChrome => "No page translation in Chrome",
        NoTranslateEdge => "No page translation in Edge",
        NoTranslateFirefox => "No page translation in Firefox",
        _ => policy,
    };

    /// <summary>The browser a translation policy is for, as the Browsers dialog names it.</summary>
    public static string TranslationBrowser(string policy) => policy switch
    {
        NoTranslateChrome => "Google Chrome",
        NoTranslateEdge => "Microsoft Edge",
        NoTranslateFirefox => "Mozilla Firefox",
        _ => policy,
    };

    // ---- browser data of the signed-in user (browser.data.clear, browser.profiles.*) ------------------

    public const string History = "history";
    public const string Cookies = "cookies";
    public const string Cache = "cache";

    public const string Profiles = "profiles";
    /// <summary>Request field of browser.profiles.remove: the <see cref="Profile.Key"/> of each profile to remove. Without it, every profile except each browser's first goes.</summary>
    public const string ProfileKeys = "ids";
    /// <summary>The protocol version from which browser.profiles.remove honours <see cref="ProfileKeys"/>; an older Client would ignore it and remove every extra profile.</summary>
    public const string SelectedRemovalVersion = "1.16";
    public static bool CanRemoveSelected(string? clientProtocolVersion) => Protocol.IsAtLeast(clientProtocolVersion, SelectedRemovalVersion);
    public const string Removed = "removed";
    public const string Files = "files";
    public const string Bytes = "bytes";
    public const string Failed = "failed";
    public const string Closed = "closedBrowsers";

    /// <summary>One browser profile as the Host lists it.</summary>
    public sealed record Profile(string Browser, string Id, string Name, string Account, bool IsFirst)
    {
        public JsonObject ToJson() => new()
        {
            ["browser"] = Browser, ["id"] = Id, ["name"] = Name, ["account"] = Account, ["first"] = IsFirst,
        };

        public static Profile FromJson(JsonNode? node) => new(
            node?["browser"]?.GetValue<string>() ?? "", node?["id"]?.GetValue<string>() ?? "", node?["name"]?.GetValue<string>() ?? "",
            node?["account"]?.GetValue<string>() ?? "", node?["first"]?.GetValue<bool>() == true);

        /// <summary>What names this profile in a removal request: the browser and its folder or section.</summary>
        public string Key => Browser + "|" + Id;

        public override string ToString() => $"{Browser} \"{Name}\"{(Account.Length > 0 ? $" ({Account})" : "")}";
    }

    public static List<Profile> ProfilesFrom(JsonObject data) =>
        (data[Profiles] as JsonArray ?? []).Select(Profile.FromJson).ToList();

    public static string DescribeCleared(JsonObject data)
    {
        // Read through the JSON text: a number is held differently when it was just built than when it arrived over the wire.
        static long Number(JsonNode? node) => node is not null && long.TryParse(node.ToJsonString(), out var value) ? value : 0;
        long files = Number(data[Files]), bytes = Number(data[Bytes]), failed = Number(data[Failed]);
        var text = files == 0 ? "nothing to clear" : FormattableString.Invariant($"{files} file(s) removed, {bytes / 1048576.0:0.#} MB freed");
        return failed > 0 ? $"{text}; {failed} file(s) could not be removed (still in use)" : text;
    }

    public static string DescribeRemoved(JsonObject data)
    {
        var removed = (data[Removed] as JsonArray ?? []).Select(Profile.FromJson).ToList();
        return removed.Count == 0 ? "no extra profiles" : $"{removed.Count} profile(s) removed: {string.Join(", ", removed)}";
    }

    /// <summary>What the policies are on one computer: "on", "off" or "partly" for each.</summary>
    public static string DescribePolicies(JsonObject data) =>
        string.Join("; ", Policies.Select(policy => $"{PolicyName(policy)}: {data[policy]?.GetValue<string>() ?? "unknown"}"));
}
