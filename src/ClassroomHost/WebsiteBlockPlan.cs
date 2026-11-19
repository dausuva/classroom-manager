// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// What the Block websites dialog asks for, decided without a window (T-253 phase 2): the domains typed, what the action is called and warns, the review lines, and what
/// each computer ends up blocking. The dialog's labels and the log lines stay in the window so they keep their translations.
/// </summary>
public sealed class WebsiteBlockPlan
{
    public const string Merge = "Add to the existing block (merge) - safe for multiple computers";
    public const string Replace = "Replace the entire block with this list";
    public const string Remove = "Remove only these domains, keep the rest";
    public const string Clear = "Clear the block entirely (ignore the domain list below)";
    public const string Draft = "Only note them in the draft list (blocks nothing)";

    /// <summary>The actions in the order the dialog lists them.</summary>
    public static readonly string[] Modes = [Merge, Replace, Remove, Clear, Draft];

    public string Mode { get; }
    public IReadOnlyList<string> Domains { get; }

    WebsiteBlockPlan(string mode, string domainsText)
    {
        Mode = mode;
        Domains = ParseDomains(domainsText);
    }

    public static WebsiteBlockPlan Build(string mode, string domainsText) => new(mode, domainsText);

    /// <summary>Domains from one-per-line or comma-separated text: trimmed, without a leading dot, lower case, each once.</summary>
    public static IReadOnlyList<string> ParseDomains(string text) =>
        text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(domain => domain.TrimStart('.').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Which action is chosen first: "Add" when domains come from the draft list or several computers are targeted (the safe one), otherwise "Replace".</summary>
    public static int DefaultModeIndex(bool hasSuggested, int targetCount) => hasSuggested || targetCount > 1 ? 0 : 1;

    /// <summary>The text shown in the domain box: what one computer blocks now, with the domains of the draft list added.</summary>
    public static string Prefill(string current, IReadOnlyList<string>? suggested) =>
        suggested is { Count: > 0 }
            ? string.Join("\n", current.Split('\n', StringSplitOptions.RemoveEmptyEntries).Concat(suggested).Distinct(StringComparer.OrdinalIgnoreCase))
            : current;

    /// <summary>What is wrong with the choices (shown to the teacher), or null.</summary>
    public string? Problem => Mode != Clear && Domains.Count == 0 ? "Enter at least one domain, or choose \"Clear the block\"." : null;

    /// <summary>The domains are only noted in the draft list; nothing is sent to a computer.</summary>
    public bool IsDraft => Mode == Draft;

    public string Title => Mode switch
    {
        Clear => "Clear website block",
        Remove => $"Unblock {Domains.Count} website(s)",
        Replace => $"Replace block with {Domains.Count} website(s)",
        _ => $"Add {Domains.Count} website(s) to the block",
    };

    public string Warning => Mode is Replace or Clear ? "This replaces or clears the managed website block on every selected computer." : "";

    public IReadOnlyList<BulkOperationReviewItem> ReviewItems =>
    [
        new("Action", Mode),
        new("Domains", Domains.Count == 0 ? "(entire managed block)" : string.Join(", ", Domains)),
    ];

    /// <summary>
    /// The add-only request takes plain website names and needs a Client that knows it; an administrator's unusual entry (a single-word intranet name, say)
    /// still goes the read-and-replace way, and so does a Client without the request.
    /// </summary>
    public bool UsesAddRequest(bool clientSupportsAdd) => Mode == Merge && clientSupportsAdd && Domains.All(domain => BrowserSites.DomainOf(domain).Length > 0);

    /// <summary>The list a computer keeps after Merge or Remove, from the list it has now (read first, so computers with different blocks each end up right).</summary>
    public IReadOnlyList<string> ListAfter(IReadOnlyList<string> existing) => Mode == Remove
        ? existing.Where(domain => !Domains.Contains(domain, StringComparer.OrdinalIgnoreCase)).ToList()
        : existing.Union(Domains, StringComparer.OrdinalIgnoreCase).ToList();
}
