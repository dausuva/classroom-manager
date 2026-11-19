// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// Tracks which entries of a fixed list are chosen while a text filter hides some of them,
/// so narrowing the visible list never drops a choice that is currently filtered out.
/// </summary>
public sealed class FilteredSelection
{
    readonly IReadOnlyList<string> _items;
    readonly HashSet<string> _chosen;

    public FilteredSelection(IReadOnlyList<string> items, IEnumerable<string>? chosen = null)
    {
        _items = items;
        _chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requested = chosen?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested is not null)
            foreach (var item in items.Where(requested.Contains)) _chosen.Add(item);
    }

    public int TotalCount => _items.Count;
    public int ChosenCount => _chosen.Count;

    /// <summary>Chosen entries in list order, including entries hidden by the current filter.</summary>
    public IReadOnlyList<string> Chosen => _items.Where(_chosen.Contains).ToList();

    public bool IsChosen(string item) => _chosen.Contains(item);

    public IReadOnlyList<string> Visible(string filter)
    {
        var text = filter.Trim();
        return text.Length == 0
            ? _items
            : _items.Where(item => item.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public void Choose(IEnumerable<string> items)
    {
        foreach (var item in items) _chosen.Add(item);
    }

    public void Unchoose(IEnumerable<string> items)
    {
        foreach (var item in items) _chosen.Remove(item);
    }
}
