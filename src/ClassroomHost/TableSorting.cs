// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace ClassroomManager.Host;

/// <summary>
/// Adds conventional click-to-sort behavior to every GridView-backed ListView, and the sort arrow every
/// sortable header shows: a faint ⇅ until the column is sorted, then ▲ or ▼. The arrow is drawn by the
/// header's style (Themes/Controls.xaml) from <see cref="SortGlyphProperty"/>, so the header's own text, its
/// translation and its screen-reader name stay as they are. DataGrid headers draw theirs from the grid's sort.
/// </summary>
public static class TableSorting
{
    public const string Sortable = "⇅";
    public const string Ascending = "▲";
    public const string Descending = "▼";

    public static readonly DependencyProperty SortGlyphProperty = DependencyProperty.RegisterAttached(
        "SortGlyph", typeof(string), typeof(TableSorting), new PropertyMetadata(""));

    public static string GetSortGlyph(DependencyObject element) => (string)element.GetValue(SortGlyphProperty);
    public static void SetSortGlyph(DependencyObject element, string value) => element.SetValue(SortGlyphProperty, value);

    static bool _initialized;

    static readonly DependencyProperty LastHeaderProperty = DependencyProperty.RegisterAttached(
        "LastHeader", typeof(GridViewColumnHeader), typeof(TableSorting));
    static readonly DependencyProperty LastPathProperty = DependencyProperty.RegisterAttached(
        "LastPath", typeof(string), typeof(TableSorting), new PropertyMetadata(""));
    static readonly DependencyProperty DirectionProperty = DependencyProperty.RegisterAttached(
        "Direction", typeof(ListSortDirection), typeof(TableSorting),
        new PropertyMetadata(ListSortDirection.Ascending));
    static readonly DependencyProperty OriginalHeaderProperty = DependencyProperty.RegisterAttached(
        "OriginalHeader", typeof(string), typeof(TableSorting), new PropertyMetadata(""));

    public static void Initialize()
    {
        // Class handlers stay for the life of the process; a second registration would sort twice per click.
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(GridViewColumnHeader), ButtonBase.ClickEvent, new RoutedEventHandler(Header_Click));
    }

    /// <summary>The path a header sorts by, or null when its column cannot be sorted.</summary>
    static string? SortPath(GridViewColumnHeader header) =>
        header.Role != GridViewColumnHeaderRole.Padding && header.Column?.DisplayMemberBinding is Binding { Path.Path: { Length: > 0 } path }
            ? path : null;


    /// <summary>
    /// Replaces a DataGrid's rows while keeping the active column sort and its header arrows.
    /// Assigning <see cref="ItemsControl.ItemsSource"/> directly makes the DataGrid discard both.
    /// </summary>
    public static void SetItemsPreservingSort(DataGrid grid, System.Collections.IEnumerable items)
    {
        var sorts = grid.Items.SortDescriptions.ToList();
        var directions = grid.Columns.Where(column => column.SortDirection is not null)
            .Select(column => (Column: column, Direction: column.SortDirection)).ToList();
        grid.ItemsSource = items;
        foreach (var sort in sorts) grid.Items.SortDescriptions.Add(sort);
        foreach (var (column, direction) in directions) column.SortDirection = direction;
    }

    static void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader header || SortPath(header) is not { } path) return;

        var list = FindAncestor<ListView>(header);
        if (list is null) return;
        var lastPath = (string)list.GetValue(LastPathProperty);
        var direction = lastPath == path &&
                        (ListSortDirection)list.GetValue(DirectionProperty) == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        var view = CollectionViewSource.GetDefaultView(list.ItemsSource ?? list.Items);
        if (view is null || !view.CanSort) return;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(path, direction));
        }

        if (list.GetValue(LastHeaderProperty) is GridViewColumnHeader previous && previous != header)
            RestoreHeader(previous);
        var original = (string)header.GetValue(OriginalHeaderProperty);
        if (original.Length == 0)
        {
            original = header.Content?.ToString() ?? header.Column!.Header?.ToString() ?? path;
            header.SetValue(OriginalHeaderProperty, original);
        }
        SetSortGlyph(header, direction == ListSortDirection.Ascending ? Ascending : Descending);
        header.ToolTip = $"Sorted by {original}, {direction.ToString().ToLowerInvariant()}. Click to reverse.";
        AutomationProperties.SetName(header,
            $"{original}, sorted {direction.ToString().ToLowerInvariant()}. Click to reverse sort order.");
        list.SetValue(LastHeaderProperty, header);
        list.SetValue(LastPathProperty, path);
        list.SetValue(DirectionProperty, direction);
    }

    static void RestoreHeader(GridViewColumnHeader header)
    {
        SetSortGlyph(header, Sortable);
        var original = (string)header.GetValue(OriginalHeaderProperty);
        if (original.Length == 0) return;
        header.ClearValue(FrameworkElement.ToolTipProperty);
        AutomationProperties.SetName(header, $"{original}. Click to sort.");
    }

    static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}

/// <summary>
/// The arrow a list header draws: its sort direction once sorted, otherwise ⇅ when its column can be sorted
/// (it shows a bound value), otherwise nothing. Values: the header's SortGlyph and its column's DisplayMemberBinding.
/// </summary>
public sealed class SortGlyphConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        values.Length > 0 && values[0] is string { Length: > 0 } glyph ? glyph
        : values.Length > 1 && values[1] is Binding { Path.Path.Length: > 0 } ? TableSorting.Sortable : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
