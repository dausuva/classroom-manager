// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;

namespace ClassroomManager.Shared;

/// <summary>
/// Websites open in the browsers of a signed-in user (<see cref="Cmd.BrowserSites"/>). The client
/// reads the address bar of every browser window, which is the tab the student is looking at, and
/// reports the site's domain. Tabs in the background are not seen.
/// </summary>
public static class BrowserSites
{
    public const string Sites = "sites";
    public const string Browser = "browser";
    public const string Title = "title";
    public const string Domain = "domain";

    /// <summary>
    /// The domain in what an address bar shows, in the form the website block list uses (lower case,
    /// no "www."), or empty when it is not a website: a search being typed, a browser page such as
    /// about:blank or edge://settings, a file, or a bare network address, which a block list of names cannot stop.
    /// Browsers often hide "https://" and "www.", so the text may start with the name itself.
    /// </summary>
    public static string DomainOf(string? addressBarText)
    {
        var text = (addressBarText ?? "").Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return "";

        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            if (!text[..scheme].Equals("http", StringComparison.OrdinalIgnoreCase) &&
                !text[..scheme].Equals("https", StringComparison.OrdinalIgnoreCase)) return "";
            text = text[(scheme + 3)..];
        }

        var end = text.IndexOfAny(['/', '?', '#']);
        var host = end < 0 ? text : text[..end];
        host = host[(host.LastIndexOf('@') + 1)..];
        var colon = host.IndexOf(':');
        if (colon >= 0)
        {
            // "example.com:8080" is a port; "about:blank" and "view-source:..." are browser pages.
            if (colon == host.Length - 1 || !host[(colon + 1)..].All(char.IsAsciiDigit)) return "";
            host = host[..colon];
        }
        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];

        var labels = host.Split('.');
        if (labels.Length < 2 || labels.Any(label => label.Length == 0 || label.StartsWith('-') || label.EndsWith('-'))) return "";
        if (!host.All(character => char.IsLetterOrDigit(character) || character is '.' or '-')) return "";
        // The last part of a name has a letter in it; all digits means a network address.
        return labels[^1].All(char.IsAsciiDigit) ? "" : host;
    }

    /// <summary>One website as the Host lists it: where it is open and in which browsers.</summary>
    public sealed record OpenSite(string Domain, IReadOnlyList<string> Computers, IReadOnlyList<string> Browsers)
    {
        /// <summary>The text of one row; the domain comes first so it can be read back with <see cref="DomainOfRow"/>.</summary>
        public string Row => $"{Domain}{RowSeparator}{Computers.Count} computer(s): {string.Join(", ", Computers)} ({string.Join(", ", Browsers)})";
    }

    public const string RowSeparator = "  —  ";

    public static string DomainOfRow(string row) =>
        row.IndexOf(RowSeparator, StringComparison.Ordinal) is var at and >= 0 ? row[..at] : row.Trim();

    /// <summary>The domains a computer reported, with the browser each was seen in.</summary>
    public static IEnumerable<(string Domain, string Browser)> FromAnswer(JsonObject data) =>
        (data[Sites] as JsonArray ?? [])
            .Select(site => (Domain: site?[Domain]?.GetValue<string>() ?? "", Browser: site?[Browser]?.GetValue<string>() ?? ""))
            .Where(site => site.Domain.Length > 0);

    /// <summary>Groups what every computer reported by website; the site open on most computers comes first.</summary>
    public static List<OpenSite> Group(IEnumerable<(string Computer, string Domain, string Browser)> seen) =>
        seen.GroupBy(site => site.Domain, StringComparer.OrdinalIgnoreCase)
            .Select(group => new OpenSite(group.Key,
                group.Select(site => site.Computer).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
                group.Select(site => site.Browser).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList()))
            .OrderByDescending(site => site.Computers.Count).ThenBy(site => site.Domain, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
