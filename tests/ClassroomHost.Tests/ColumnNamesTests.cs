// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// T-176: "Columns..." crashed in Lithuanian because the English name of a column was found by reversing
/// its translation, and "State" and "Status" are both "Būsena".
/// </summary>
[Collection(nameof(LanguageSwitching))]
public sealed class ColumnNamesTests
{
    sealed record Row(string A, string B, string C);

    [Fact]
    public void Columns_whose_titles_translate_alike_keep_their_own_english_names() => OnSta(() =>
    {
        var previous = Loc.Current;
        Loc.Use(Loc.BuiltIn(Loc.Lithuanian));
        var window = new Window { Left = -20000, Top = -20000, Width = 600, Height = 400, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            var grid = new DataGrid { AutoGenerateColumns = false, ItemsSource = new List<Row> { new("a", "b", "c") } };
            foreach (var (title, path) in new[] { ("State", nameof(Row.A)), ("Status", nameof(Row.B)), ("Computer", nameof(Row.C)) })
                grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(path), Width = 100 });
            window.Content = grid;
            window.Show();
            window.UpdateLayout();

            // The precondition of the crash: the two titles really are the same on screen.
            Assert.Equal(Loc.T("State"), Loc.T("Status"));
            // The translator may already have visited the window when it loaded (another test registers it
            // for the whole application), so what is checked is the result, not that this call changed it.
            UiTranslator.TranslateTree(grid);
            Assert.Equal(Loc.T("State"), grid.Columns[0].Header);
            Assert.Equal(Loc.T("Status"), grid.Columns[1].Header);

            var named = TableColumns.Named(grid);

            Assert.Equal(["State", "Status", "Computer"], named.Select(column => column.Name));
        }
        finally { window.Close(); Loc.Use(previous); }
    });

    [Fact]
    public void Hiding_one_of_two_alike_columns_leaves_the_other_alone() => OnSta(() =>
    {
        var previous = Loc.Current;
        Loc.Use(Loc.BuiltIn(Loc.Lithuanian));
        var window = new Window { Left = -20000, Top = -20000, Width = 600, Height = 400, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            var grid = new DataGrid { AutoGenerateColumns = false, ItemsSource = new List<Row> { new("a", "b", "c") } };
            grid.Columns.Add(new DataGridTextColumn { Header = "State", Binding = new Binding(nameof(Row.A)), Width = 100 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding(nameof(Row.B)), Width = 100 });
            window.Content = grid;
            window.Show();
            window.UpdateLayout();
            UiTranslator.TranslateTree(grid);

            TableColumns.Apply(grid, ["Status"]);

            Assert.Equal(Visibility.Visible, grid.Columns[0].Visibility);
            Assert.Equal(Visibility.Collapsed, grid.Columns[1].Visibility);
        }
        finally { window.Close(); Loc.Use(previous); }
    });

    [Fact]
    public void English_windows_and_untranslated_columns_keep_working() => OnSta(() =>
    {
        var previous = Loc.Current;
        Loc.Use(Translation.None);
        try
        {
            var grid = new DataGrid { AutoGenerateColumns = false };
            grid.Columns.Add(new DataGridTextColumn { Header = "State" });
            grid.Columns.Add(new DataGridTextColumn { Header = "Something the catalogue does not know" });
            grid.Columns.Add(new DataGridTemplateColumn { Header = new Button() });

            var named = TableColumns.Named(grid);

            Assert.Equal(["State", "Something the catalogue does not know"], named.Select(column => column.Name));
        }
        finally { Loc.Use(previous); }
    });

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
