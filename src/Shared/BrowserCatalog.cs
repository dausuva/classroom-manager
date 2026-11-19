// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security;
using System.Text.Json.Nodes;

namespace ClassroomManager.Shared;

/// <summary>
/// Which web browsers a computer has and which one opens web links (browser.list, browser.default.set).
///
/// Windows protects each user's own choice of default browser, so no program may simply set it. What an
/// administrator can do is give Windows a default associations file through the policy
/// <see cref="DefaultPolicyValue"/>: Windows applies it when a user signs in. So a new default takes
/// effect at the next sign-in, and stopping it puts back whatever the policy was before.
/// </summary>
public static class BrowserCatalog
{
    /// <summary>A browser Classroom Manager knows by name: its id, the name shown, its program file and the
    /// registration name Windows uses for its web links when the browser does not report one.</summary>
    public sealed record Known(string Id, string Name, string Exe, string LinkProgId, bool Main);

    public static readonly IReadOnlyList<Known> All =
    [
        new("chrome", "Google Chrome", "chrome.exe", "ChromeHTML", true),
        new("edge", "Microsoft Edge", "msedge.exe", "MSEdgeHTM", true),
        new("firefox", "Mozilla Firefox", "firefox.exe", "", true),
        new("brave", "Brave", "brave.exe", "BraveHTML", false),
        new("opera", "Opera", "opera.exe", "", false),
        new("vivaldi", "Vivaldi", "vivaldi.exe", "", false),
    ];

    public static IEnumerable<Known> Main => All.Where(browser => browser.Main);

    public static Known? Find(string id) => All.FirstOrDefault(browser => browser.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The known browser a program file belongs to, or null. Opera starts through a launcher.exe in its own folder.</summary>
    public static Known? ByExe(string path)
    {
        var file = Path.GetFileName(path.Trim().Trim('"'));
        if (file.Equals("launcher.exe", StringComparison.OrdinalIgnoreCase) && path.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return Find("opera");
        return All.FirstOrDefault(browser => browser.Exe.Equals(file, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The browser behind a registration name such as "ChromeHTML" or "FirefoxURL-308046B0AF4A39CB" (the
    /// part after the dash depends on where Firefox is installed), or null when it is none of the known ones.
    /// </summary>
    public static Known? ByProgId(string progId)
    {
        var id = progId.Trim();
        if (id.Length == 0) return null;
        if (id.StartsWith("Firefox", StringComparison.OrdinalIgnoreCase)) return Find("firefox");
        if (id.StartsWith("Opera", StringComparison.OrdinalIgnoreCase)) return Find("opera");
        if (id.StartsWith("Vivaldi", StringComparison.OrdinalIgnoreCase)) return Find("vivaldi");
        if (id.StartsWith("Brave", StringComparison.OrdinalIgnoreCase)) return Find("brave");
        if (id.StartsWith("Chrome", StringComparison.OrdinalIgnoreCase)) return Find("chrome");
        if (id.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase)) return Find("edge");
        return null;
    }

    // ---- browser.list ----------------------------------------------------------------------------------

    public const string Browsers = "browsers";
    public const string Default = "default";              // id of the browser that opens web links for the signed-in user ("" when not known or nobody is signed in)
    public const string DefaultProgId = "defaultProgId";
    public const string Managed = "managed";              // id of the browser Classroom Manager sets as default at sign-in, "" when none
    public const string Browser = "browser";              // browser.default.set: the id, or "" to stop setting one

    /// <summary>One browser found on a computer. Registered means Windows lists it under Default apps.</summary>
    public sealed record Installed(string Id, string Name, string Path, string Version, string LinkProgId, string FileProgId, bool Registered)
    {
        public JsonObject ToJson() => new()
        {
            ["id"] = Id, ["name"] = Name, ["path"] = Path, ["version"] = Version,
            ["linkProgId"] = LinkProgId, ["fileProgId"] = FileProgId, ["registered"] = Registered,
        };

        public static Installed FromJson(JsonNode? node) => new(
            Text(node, "id"), Text(node, "name"), Text(node, "path"), Text(node, "version"),
            Text(node, "linkProgId"), Text(node, "fileProgId"), node?["registered"]?.GetValue<bool>() == true);

        /// <summary>"Google Chrome 141.0.7390.66" - the version's first part is enough to compare computers.</summary>
        public string Label => Version.Length > 0 ? $"{Name} {Version}" : Name;
    }

    static string Text(JsonNode? node, string name) => node?[name]?.GetValue<string>() ?? "";

    public static List<Installed> InstalledFrom(JsonObject data) => (data[Browsers] as JsonArray ?? []).Select(Installed.FromJson).ToList();

    /// <summary>Main browsers missing from a computer's answer, by name.</summary>
    public static List<string> MissingMain(IEnumerable<Installed> installed)
    {
        var have = installed.Select(browser => browser.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Main.Where(browser => !have.Contains(browser.Id)).Select(browser => browser.Name).ToList();
    }

    public static string Describe(JsonObject data)
    {
        var installed = InstalledFrom(data);
        var text = installed.Count == 0 ? "No browser found." : $"Browsers: {string.Join(", ", installed.Select(browser => browser.Label))}.";
        var missing = MissingMain(installed);
        if (missing.Count > 0) text += $" Missing: {string.Join(", ", missing)}.";
        var current = Find(data[Default]?.GetValue<string>() ?? "");
        if (current is not null) text += $" Opens web links: {current.Name}.";
        var managed = Find(data[Managed]?.GetValue<string>() ?? "");
        if (managed is not null) text += $" Default set at sign-in: {managed.Name}.";
        return text;
    }

    // ---- browser.default.set ---------------------------------------------------------------------------

    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\System";
    public const string DefaultPolicyValue = "DefaultAssociationsConfiguration";

    /// <summary>
    /// The default associations file that makes <paramref name="browser"/> open web links and web pages.
    /// Windows reads Identifier (a link type or file extension), ProgId (the browser's registration for it)
    /// and ApplicationName.
    /// </summary>
    public static string AssociationsXml(Installed browser)
    {
        var file = browser.FileProgId.Length > 0 ? browser.FileProgId : browser.LinkProgId;
        var name = SecurityElement.Escape(browser.Name);
        string Line(string identifier, string progId) =>
            $"  <Association Identifier=\"{identifier}\" ProgId=\"{SecurityElement.Escape(progId)}\" ApplicationName=\"{name}\" />";
        string[] lines =
        [
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<DefaultAssociations>",
            Line("http", browser.LinkProgId),
            Line("https", browser.LinkProgId),
            Line(".htm", file),
            Line(".html", file),
            "</DefaultAssociations>",
            "",
        ];
        return string.Join("\r\n", lines);
    }

    /// <summary>The browser a default associations file makes the default (by its https line), or null.</summary>
    public static Known? FromAssociationsXml(string xml)
    {
        var at = xml.IndexOf("Identifier=\"https\"", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var start = xml.IndexOf("ProgId=\"", at, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += "ProgId=\"".Length;
        var end = xml.IndexOf('"', start);
        return end < 0 ? null : ByProgId(xml[start..end]);
    }
}
