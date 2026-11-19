// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// What the Block apps dialog asks for, decided without a window (T-253 phase 2): the process names from what was ticked and typed, the review lines, and what
/// happens to the Host's default list. The dialog's labels stay in the window so they keep their translations.
/// </summary>
public sealed class AppBlockPlan
{
    public bool Clear { get; }
    public IReadOnlyList<string> Names { get; }
    public bool Remember { get; }
    public bool ReplaceCatalog { get; }

    AppBlockPlan(bool clear, string selected, string typed, bool includeDefaults, IReadOnlyList<string> catalog, bool remember, bool replaceCatalog)
    {
        Clear = clear;
        Remember = remember;
        ReplaceCatalog = replaceCatalog;
        var entered = selected + "\n" + typed + (includeDefaults ? "\n" + string.Join("\n", catalog) : "");
        Names = entered
            .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeEntry)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static AppBlockPlan Build(bool clear, string selected, string typed, bool includeDefaults, IReadOnlyList<string> catalog, bool remember, bool replaceCatalog) =>
        new(clear, selected, typed, includeDefaults, catalog, remember, replaceCatalog);

    /// <summary>A name or path as the Client matches it: quotes removed, and ".exe" added to a bare name (a path, or a name that has it, is kept).</summary>
    public static string NormalizeEntry(string value)
    {
        value = value.Trim().Trim('"');
        if (value.Length == 0) return "";
        var hasPath = value.Contains('\\') || value.Contains('/');
        return hasPath || value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : value + ".exe";
    }

    /// <summary>What is wrong with the choices (shown to the teacher), or null.</summary>
    public string? Problem => !Clear && Names.Count == 0 ? "Enter at least one process name, or tick \"Clear the block\"." : null;

    public string Title => Clear ? "Clear app block" : $"Block {Names.Count} app(s)";

    public string Warning => "The selected names become the complete permanent app block on every selected computer.";

    public IReadOnlyList<BulkOperationReviewItem> ReviewItems =>
    [
        new("Action", Clear ? "Clear the permanent app block" : "Replace the permanent app block"),
        new("Process names", Clear ? "(all blocked names)" : string.Join(", ", Names)),
        new("Host defaults", !Clear && Remember ? (ReplaceCatalog ? "Replace with these names" : "Add these names") : "Do not change"),
    ];

    /// <summary>The Host's default list after this, or null when it stays as it is: the applied names added to it (or replacing it), sorted, each once.</summary>
    public List<string>? CatalogAfter(IReadOnlyList<string> current) => Clear || !Remember
        ? null
        : (ReplaceCatalog ? Names : current.Concat(Names)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
}
