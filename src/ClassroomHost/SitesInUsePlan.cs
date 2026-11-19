// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>What to do with the websites found open in the students' browsers.</summary>
public enum SitesInUseAction { Block, DraftTicked, DraftAll }

/// <summary>
/// What the Websites in use dialog asks for, decided without a window (T-253 phase 2): which websites, whether to block or only note them, on which computers,
/// what each computer adds, and the review lines. The dialog's labels and the log lines stay in the window so they keep their translations.
/// </summary>
public sealed class SitesInUsePlan
{
    public const string Everywhere = "Every selected computer", WhereOpen = "Only the computers where the website is open";
    public const string BlockNow = "Block the ticked websites now", DraftTicked = "Only note the ticked websites in the draft list";
    public const string DraftAll = "Note every open website in the draft list (ignores the ticks)";

    public SitesInUseAction Action { get; }
    public IReadOnlyList<string> Blocked { get; }
    public bool OnlyWhereOpen { get; }
    readonly IReadOnlyDictionary<ClientInfo, HashSet<string>> _openOn;

    SitesInUsePlan(string what, string where, string ticked, IReadOnlyDictionary<ClientInfo, HashSet<string>> openOn)
    {
        Action = what == DraftAll ? SitesInUseAction.DraftAll : what == DraftTicked ? SitesInUseAction.DraftTicked : SitesInUseAction.Block;
        Blocked = ticked.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(BrowserSites.DomainOfRow).Where(domain => domain.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        OnlyWhereOpen = where == WhereOpen;
        _openOn = openOn;
    }

    // what: the action chosen; where: where to block; ticked: the ticked rows, one per line; openOn: the domains open on each computer.
    public static SitesInUsePlan Build(string what, string where, string ticked, IReadOnlyDictionary<ClientInfo, HashSet<string>> openOn) => new(what, where, ticked, openOn);

    /// <summary>What is wrong with the choices (shown to the teacher), or null. Noting every open website needs no tick.</summary>
    public string? Problem => Action != SitesInUseAction.DraftAll && Blocked.Count == 0 ? "Tick at least one website to block." : null;

    /// <summary>The computers the block goes to: all of those given, or only those where one of the ticked websites is open.</summary>
    public List<ClientInfo> Targets(IReadOnlyList<ClientInfo> selected) => OnlyWhereOpen
        ? selected.Where(client => _openOn.TryGetValue(client, out var open) && Blocked.Any(open.Contains)).ToList()
        : selected.ToList();

    /// <summary>The domains this computer is given: all that were ticked, or only the ticked ones open on it.</summary>
    public IReadOnlyList<string> AddFor(ClientInfo client) => OnlyWhereOpen ? Blocked.Where(_openOn[client].Contains).ToList() : Blocked;

    public string Title => $"Block {Blocked.Count} website(s) in use";

    public IReadOnlyList<BulkOperationReviewItem> ReviewItems =>
    [
        new("Action", "Add to the existing website block"),
        new("Domains", string.Join(", ", Blocked)),
        new("Where", OnlyWhereOpen ? "Each domain only on the computers where it is open" : "Every domain on every selected computer"),
    ];
}
