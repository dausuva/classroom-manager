// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// What the Interface settings dialog decides (T-253 phase 2): the language choices, which choice a stored value means, and how the ticked quick actions and Teacher-mode
/// commands become the stored lists. The dialog itself stays in the window so its labels keep their translations.
/// </summary>
public static class InterfaceSettingsPlan
{
    /// <summary>The language codes offered ("System" first) and the label shown for each, in the same order.</summary>
    public sealed record LanguageChoices(List<string> Codes, string[] Labels)
    {
        public int IndexOf(string code) => Math.Max(0, Codes.IndexOf(code));

        /// <summary>The code of the label picked; the first ("System") when the label is not found.</summary>
        public string CodeFor(string label) => Codes[Math.Max(0, Array.IndexOf(Labels, label))];
    }

    /// <summary>"System" follows Windows, then the built-in languages and the files under Settings > Languages; a stored language no longer on offer stays selectable.</summary>
    public static LanguageChoices Languages(string stored, IReadOnlyList<(string Language, string Name, string? File)> available)
    {
        var codes = new List<string> { Loc.Windows };
        codes.AddRange(available.Select(language => language.Language));
        if (!codes.Contains(stored, StringComparer.OrdinalIgnoreCase)) codes.Add(stored);
        var labels = codes.Select(code => code == Loc.Windows ? "Same as Windows"
            : available.FirstOrDefault(language => language.Language == code) is { Name.Length: > 0 } known ? $"{known.Name} ({code})" : code).ToArray();
        return new LanguageChoices(codes, labels);
    }

    /// <summary>The position of the stored theme; the second (Light) when it is not one of the choices.</summary>
    public static int ThemeIndex(string[] choices, string theme) => Array.IndexOf(choices, theme) is var index && index >= 0 ? index : 1;

    /// <summary>The labels a multi-select returns (one per line) as a set.</summary>
    public static HashSet<string> Chosen(string labels) =>
        labels.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static List<string> QuickActionIds(string chosenLabels)
    {
        var chosen = Chosen(chosenLabels);
        return QuickActionCatalog.All.Where(action => chosen.Contains(action.Label)).Select(action => action.Id).ToList();
    }

    /// <summary>
    /// The Teacher-mode commands to keep: the stored ones this dialog does not offer (a module that is off hides them) stay as they are, and the ticked labels of each group are added.
    /// <paramref name="chosenForGroup"/> gives the labels ticked in the group at that position of <paramref name="groups"/>.
    /// </summary>
    public static List<string> TeacherVisible(IEnumerable<string> stored, Func<string, bool> offered, IReadOnlyList<string> groups, Func<int, string> chosenForGroup)
    {
        var visible = stored.Where(id => !offered(id)).ToList();
        for (var i = 0; i < groups.Count; i++)
        {
            var chosen = Chosen(chosenForGroup(i));
            visible.AddRange(TeacherCommandCatalog.All.Where(command => command.Group == groups[i] && chosen.Contains(command.Label)).Select(command => command.Id));
        }
        return TeacherCommandCatalog.Normalize(visible);
    }
}
