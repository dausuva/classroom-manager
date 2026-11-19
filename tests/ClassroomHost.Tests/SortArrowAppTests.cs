// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// T-078 as the app has it: the styles at application level, where a table header's template is applied
/// before its column is set. A trigger on the column kept every arrow hidden there although a test with
/// the styles on its own window passed (SortArrowTests).
/// </summary>
public sealed class SortArrowAppTests
{
    sealed record Row(string Name);

    [Fact]
    public void A_table_in_the_app_shows_the_arrow_on_its_columns_and_none_on_the_empty_header() => OnSta(() =>
    {
        // The application belongs to the thread that made it; another test may have made it first.
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!app.Dispatcher.CheckAccess()) return;
        foreach (var theme in new[] { "Light", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/ClassroomHost;component/Themes/{theme}.xaml", UriKind.Relative) });
        var window = new Window { Left = -20000, Top = -20000, Width = 600, Height = 400, ShowInTaskbar = false, ShowActivated = false };
        var grid = new DataGrid { AutoGenerateColumns = false, ItemsSource = new List<Row> { new("a") } };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)), Width = 100 });
        window.Content = grid;
        window.Show();
        try
        {
            window.UpdateLayout();
            var headers = Find<DataGridColumnHeader>(grid).ToList();
            var named = headers.Single(header => header.Column is not null);
            Assert.Equal(("⇅", Visibility.Visible), Glyph(named));
            foreach (var empty in headers.Where(header => header.Column is null))
                Assert.Equal(Visibility.Collapsed, Glyph(empty).Visibility);
        }
        finally { window.Close(); }
    });

    static (string Text, Visibility Visibility) Glyph(Control header) =>
        header.Template.FindName("SortGlyph", header) is TextBlock glyph ? (glyph.Text, glyph.Visibility) : ("", Visibility.Collapsed);

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
        var thread = new Thread(() => { try { test(); } catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
