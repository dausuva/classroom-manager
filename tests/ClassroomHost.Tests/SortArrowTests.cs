// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-078: every sortable column shows that it can be sorted, and which way it is sorted.</summary>
public sealed class SortArrowTests
{
    sealed record Row(string Name, int Size);

    [Fact]
    public void Lists_and_tables_show_a_faint_arrow_until_sorted_then_the_direction() => OnSta(() =>
    {
        TableSorting.Initialize();
        var window = new Window { Left = -20000, Top = -20000, Width = 600, Height = 400, ShowInTaskbar = false, ShowActivated = false };
        foreach (var theme in new[] { "Light", "Controls" })
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/ClassroomHost;component/Themes/{theme}.xaml", UriKind.Relative) });

        var rows = new List<Row> { new("b", 2), new("a", 1) };
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Name", DisplayMemberBinding = new Binding(nameof(Row.Name)) });
        view.Columns.Add(new GridViewColumn { Header = "Size", DisplayMemberBinding = new Binding(nameof(Row.Size)) });
        var list = new ListView { View = view, ItemsSource = rows, Height = 150 };
        var grid = new DataGrid { AutoGenerateColumns = false, ItemsSource = rows, Height = 150 };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Size", Binding = new Binding(nameof(Row.Size)), CanUserSort = false });
        window.Content = new StackPanel { Children = { list, grid } };
        window.Show();
        try
        {
            Settle(window);
            var listHeaders = Find<GridViewColumnHeader>(list).Where(header => header.Column is not null).OrderBy(header => view.Columns.IndexOf(header.Column)).ToList();
            Assert.Equal(["⇅", "⇅"], listHeaders.Select(Glyph));

            listHeaders[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.UpdateLayout();
            Assert.Equal(["▲", "⇅"], listHeaders.Select(Glyph));
            Assert.Equal("Name", listHeaders[0].Content);   // the text itself is left as it is
            listHeaders[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            window.UpdateLayout();
            Assert.Equal(["⇅", "▲"], listHeaders.Select(Glyph));

            var gridHeaders = Find<DataGridColumnHeader>(grid).Where(header => header.Column is not null).OrderBy(header => header.DisplayIndex).ToList();
            Assert.Equal("⇅", Glyph(gridHeaders[0]));
            Assert.Equal(Visibility.Collapsed, GlyphBlock(gridHeaders[1])!.Visibility);   // not sortable: no arrow
            grid.Columns[0].SortDirection = ListSortDirection.Descending;
            window.UpdateLayout();
            Assert.Equal("▼", Glyph(gridHeaders[0]));
        }
        finally { window.Close(); }
    });

    /// <summary>Layout, then the Loaded events WPF raises afterwards through the dispatcher.</summary>
    static void Settle(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    static TextBlock? GlyphBlock(Control header) => header.Template?.FindName("SortGlyph", header) as TextBlock;
    static string Glyph(Control header) => GlyphBlock(header)?.Text ?? "(no arrow)";

    static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var deeper in Find<T>(child)) yield return deeper;
        }
    }

    static void OnSta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
