// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>How the teacher arranged one tab of the main toolbar: the actions of each row, in order.</summary>
public sealed class ToolbarLayout
{
    public List<string> Top { get; set; } = new();
    public List<string> More { get; set; } = new();
    public List<string> Hidden { get; set; } = new();

    public ToolbarLayout Copy() => new() { Top = [.. Top], More = [.. More], Hidden = [.. Hidden] };
}

/// <summary>
/// Turns a saved arrangement into the rows to show. The program's own layout is the starting point:
/// an action the saved arrangement does not know (new in this version) keeps its default row and goes
/// last in it; a saved action that no longer exists is dropped; an action is in one row only.
/// </summary>
public static class ToolbarArrangement
{
    /// <summary>More stays alphabetical; the top row keeps the user's chosen order.</summary>
    public static List<string> OrderMore(IEnumerable<string> ids, Func<string, string>? translate = null) =>
        ids.OrderBy(id => (translate ?? (label => label))(Label(id)).Replace("_", ""), StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Carry saved hidden choices to the new groups when commands move between tabs.</summary>
    public static void Relocate(Dictionary<string, ToolbarLayout> saved, IReadOnlyList<ToolbarTabDefaults> defaults)
    {
        var moves = new (string From, string To, string Label, string? NewLabel)[]
        {
            ("Computers", "Classroom", "Sync time...", null),
            ("Classroom", "Computers", "Power...", null),
            ("Classroom", "Computers", "Cancel pending shutdown", null),
            ("Software", "Computers", "Windows Update...", null),
            ("Classroom", "Classroom", "Hide messages", null),
            ("Software", "Classroom", "Open website...", null),
            ("Software", "Classroom", "Open program...", null),
            ("Restrictions", "Software", "Browsers...", null),
            ("Settings", "Settings", "Languages...", null),
            ("Settings", "Help", "Help", null),
            ("Settings", "Help", "About...", null),
            ("Settings", "Help", "Send feedback...", null),
            ("Settings", "Help", "Review saved feedback...", null),
            ("Settings", "Help", "Open-source licenses...", null),
            ("Overview", "Overview", "Lock screen...", "Lock screen"),
            ("Overview", "Overview", "Message...", "Show message"),
        };
        foreach (var move in moves)
        {
            var oldId = Id(move.From, move.Label);
            var newId = Id(move.To, move.NewLabel ?? move.Label);
            if (!saved.TryGetValue(move.From, out var source)) continue;
            bool hidden = source.Hidden.Contains(oldId, StringComparer.OrdinalIgnoreCase);
            if (!hidden && !source.Top.Contains(oldId, StringComparer.OrdinalIgnoreCase) && !source.More.Contains(oldId, StringComparer.OrdinalIgnoreCase)) continue;
            var targetDefaults = defaults.FirstOrDefault(tab => tab.Tab == move.To);
            if (targetDefaults is null || !targetDefaults.Top.Concat(targetDefaults.More).Contains(newId)) continue;
            foreach (var row in new[] { source.Top, source.More, source.Hidden }) row.RemoveAll(id => id.Equals(oldId, StringComparison.OrdinalIgnoreCase));
            if (!saved.TryGetValue(move.To, out var target))
                saved[move.To] = target = Resolve(targetDefaults.Top, targetDefaults.More, null);
            foreach (var row in new[] { target.Top, target.More, target.Hidden }) row.RemoveAll(id => id.Equals(newId, StringComparison.OrdinalIgnoreCase));
            if (hidden) target.Hidden.Add(newId);
            else
            {
                var defaultRow = targetDefaults.Top.Contains(newId) ? targetDefaults.Top : targetDefaults.More;
                var row = targetDefaults.Top.Contains(newId) ? target.Top : target.More;
                // Anchor moved commands beside their default neighbors without resetting the rest of a custom row.
                var before = defaultRow.TakeWhile(id => id != newId).Reverse().FirstOrDefault(row.Contains);
                var after = defaultRow.SkipWhile(id => id != newId).Skip(1).FirstOrDefault(row.Contains);
                if (before is not null) row.Insert(row.IndexOf(before) + 1, newId);
                else if (after is not null) row.Insert(row.IndexOf(after), newId);
                else row.Add(newId);
            }
        }
    }

    /// <summary>
    /// Actions that can be moved but never hidden: the way back to this arrangement, and the settings
    /// window that also leads there. A saved arrangement that hid one shows it in its default row again.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverHidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Id("Settings", "Arrange toolbar..."),
        Id("Settings", "Interface settings..."),
    };

    public static ToolbarLayout Resolve(IReadOnlyList<string> defaultTop, IReadOnlyList<string> defaultMore, ToolbarLayout? saved)
    {
        if (saved is null) return new ToolbarLayout { Top = [.. defaultTop], More = [.. defaultMore] };
        var known = defaultTop.Concat(defaultMore).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> Take(IEnumerable<string> ids) => ids.Where(id => known.Contains(id) && placed.Add(id)).ToList();

        var result = new ToolbarLayout { Top = Take(saved.Top), More = Take(saved.More), Hidden = Take(saved.Hidden.Where(id => !NeverHidden.Contains(id))) };
        result.Top.AddRange(defaultTop.Where(placed.Add));
        result.More.AddRange(defaultMore.Where(placed.Add));
        return result;
    }

    /// <summary>
    /// Whether a tab of the toolbar is left out: it has actions, but none of them is shown (Teacher mode hides
    /// them, or the teacher hid them all). The Settings tab always stays; the way back is there.
    /// </summary>
    public static bool HidesTab(string tab, int actions, int shown) =>
        !tab.Equals("Settings", StringComparison.OrdinalIgnoreCase) && actions > 0 && shown == 0;

    /// <summary>The id of a toolbar action: its tab and its English label without the shortcut mark.</summary>
    public static string Id(string tab, string englishLabel) => tab + "|" + englishLabel.Replace("_", "").Trim();

    /// <summary>The label part of an id, for showing in the arrange window.</summary>
    public static string Label(string id) => id[(id.IndexOf('|') + 1)..];
}
