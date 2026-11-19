// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>A website the teacher noted down to block later; it is kept in the Host and reaches no computer.</summary>
public sealed class WebsiteDraft
{
    public string Domain { get; set; } = "";
    public DateTime Added { get; set; }
    /// <summary>Where the teacher noted it: "Websites in use", "Block websites".</summary>
    public string Source { get; set; } = "";
}

/// <summary>
/// The draft list of websites. Nothing here is sent to a computer: a draft only becomes a block when the
/// teacher chooses it in the ordinary Block websites dialog, which asks for the same confirmation as always.
/// </summary>
public static class WebsiteDrafts
{
    public const string SourceInUse = "Websites in use";
    public const string SourceBlockDialog = "Block websites";

    /// <summary>Notes the domains down; ones already in the list, and text that is not a website, are skipped. Returns how many were added.</summary>
    public static int Add(List<WebsiteDraft> drafts, IEnumerable<string> domains, string source, DateTime now)
    {
        int added = 0;
        foreach (var text in domains)
        {
            var domain = DomainOf(text);
            if (domain.Length == 0 || drafts.Any(d => d.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))) continue;
            drafts.Add(new WebsiteDraft { Domain = domain, Added = now, Source = source });
            added++;
        }
        return added;
    }

    public static int Remove(List<WebsiteDraft> drafts, IEnumerable<string> domains)
    {
        var gone = domains.Select(DomainOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return drafts.RemoveAll(d => gone.Contains(d.Domain));
    }

    /// <summary>The list as saved: blank and repeated entries dropped, domains lower-case.</summary>
    public static List<WebsiteDraft> Normalize(IEnumerable<WebsiteDraft>? drafts)
    {
        var clean = new List<WebsiteDraft>();
        foreach (var draft in drafts ?? [])
            Add(clean, [draft.Domain], draft.Source, draft.Added);
        return clean;
    }

    /// <summary>One line for a list: the domain first, then the day it was noted.</summary>
    public static string Row(WebsiteDraft draft) =>
        draft.Added == default ? draft.Domain : draft.Domain + BrowserSites.RowSeparator + draft.Added.ToString("yyyy-MM-dd");

    /// <summary>The domain back from a <see cref="Row"/>.</summary>
    public static string DomainOfRow(string row) => DomainOf(BrowserSites.DomainOfRow(row));

    static string DomainOf(string text)
    {
        var plain = text.Trim().TrimStart('.').ToLowerInvariant();
        // A single-word intranet name is allowed in the block list but not by the address-bar reader; keep it as typed.
        var domain = BrowserSites.DomainOf(plain);
        return domain.Length > 0 ? domain : plain.Length > 0 && !plain.Contains('.') && plain.All(c => char.IsLetterOrDigit(c) || c is '-') && !plain.All(char.IsAsciiDigit) ? plain : "";
    }
}
