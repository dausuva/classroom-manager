// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;

namespace ClassroomManager.Host;

/// <summary>Lets the user show or hide DataGrid columns and remembers the hidden ones per window.</summary>
public static class TableColumns
{
    /// <summary>
    /// Returns the headers to hide for the chosen visible set, in table order,
    /// or null when the choice would leave no column visible.
    /// </summary>
    public static List<string>? Hidden(IReadOnlyList<string> headers, IEnumerable<string> visible)
    {
        var keep = visible.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hidden = headers.Where(header => !keep.Contains(header)).ToList();
        return hidden.Count == headers.Count ? null : hidden;
    }

    /// <summary>
    /// The columns that have a text title, with their English names, in table order. The name is what is
    /// remembered and compared, whatever language the titles are shown in.
    /// </summary>
    public static IReadOnlyList<(string Name, DataGridColumn Column)> Named(DataGrid grid) =>
        grid.Columns.Where(column => column.Header is string)
            .Select(column => (UiTranslator.ColumnName(column), column)).ToList();

    public static void Apply(DataGrid grid, IEnumerable<string> hidden)
    {
        var hiddenSet = hidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, column) in Named(grid))
            column.Visibility = hiddenSet.Contains(name) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Asks which columns to show and applies the answer; returns true when the choice changed.</summary>
    public static bool Choose(Window owner, string title, DataGrid grid, WindowFilterSettings state)
    {
        // The English names: they are what is remembered, whatever language the titles are shown in.
        var headers = Named(grid).Select(column => column.Name).ToList();
        var visible = headers.Where(header => !state.HiddenColumns.Contains(header, StringComparer.OrdinalIgnoreCase)).ToList();
        var dialog = new FormDialog(title, owner)
            .MultiSelect("columns", "Columns to show", headers, visible);
        if (!dialog.Ask("Save")) return false;

        var hidden = Hidden(headers, dialog.S("columns").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        if (hidden is null)
        {
            MessageBox.Show(owner, "Keep at least one column visible.", title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        state.HiddenColumns = hidden;
        Apply(grid, hidden);
        return true;
    }
}
