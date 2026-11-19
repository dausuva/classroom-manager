// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class TableSortingTests
{
    sealed record Row(string Name, int Size);

    [Fact]
    public void Replacing_rows_keeps_the_active_sort_and_header_direction() => RunOnStaThread(() =>
    {
        var grid = new DataGrid { AutoGenerateColumns = false };
        var name = new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)) };
        var size = new DataGridTextColumn { Header = "Size", Binding = new Binding(nameof(Row.Size)) };
        grid.Columns.Add(name);
        grid.Columns.Add(size);
        grid.ItemsSource = new List<Row> { new("b", 2), new("a", 1) };
        grid.Items.SortDescriptions.Add(new SortDescription(nameof(Row.Size), ListSortDirection.Descending));
        size.SortDirection = ListSortDirection.Descending;

        TableSorting.SetItemsPreservingSort(grid, new List<Row> { new("c", 5), new("d", 9), new("e", 7) });

        Assert.Equal(ListSortDirection.Descending, size.SortDirection);
        Assert.Null(name.SortDirection);
        Assert.Equal(new SortDescription(nameof(Row.Size), ListSortDirection.Descending),
            Assert.Single(grid.Items.SortDescriptions));
        Assert.Equal(new[] { 9, 7, 5 }, grid.Items.Cast<Row>().Select(row => row.Size));
    });

    [Fact]
    public void Replacing_rows_of_an_unsorted_grid_leaves_it_unsorted() => RunOnStaThread(() =>
    {
        var grid = new DataGrid { AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)) });
        grid.ItemsSource = new List<Row> { new("b", 2) };

        TableSorting.SetItemsPreservingSort(grid, new List<Row> { new("z", 1), new("a", 2) });

        Assert.Empty(grid.Items.SortDescriptions);
        Assert.Equal(new[] { "z", "a" }, grid.Items.Cast<Row>().Select(row => row.Name));
    });

    [Fact]
    public void Hidden_columns_are_the_unchosen_headers_in_table_order()
    {
        string[] headers = ["Program", "Version(s)", "Publisher", "Silent"];

        Assert.Equal(["Version(s)", "Silent"], TableColumns.Hidden(headers, ["publisher", "Program"]));
        Assert.Empty(TableColumns.Hidden(headers, headers)!);
    }

    [Fact]
    public void Hiding_every_column_is_rejected()
    {
        Assert.Null(TableColumns.Hidden(["Program", "Publisher"], []));
        Assert.Null(TableColumns.Hidden(["Program", "Publisher"], ["Not a column"]));
    }

    [Fact]
    public void Applying_hidden_columns_collapses_them_and_restores_the_rest() => RunOnStaThread(() =>
    {
        var grid = new DataGrid { AutoGenerateColumns = false };
        var name = new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)) };
        var size = new DataGridTextColumn { Header = "Size", Binding = new Binding(nameof(Row.Size)) };
        grid.Columns.Add(name);
        grid.Columns.Add(size);

        TableColumns.Apply(grid, ["size"]);
        Assert.Equal(System.Windows.Visibility.Visible, name.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, size.Visibility);

        TableColumns.Apply(grid, ["Name"]);
        Assert.Equal(System.Windows.Visibility.Collapsed, name.Visibility);
        Assert.Equal(System.Windows.Visibility.Visible, size.Visibility);
    });

    // WPF controls can only be created on a single-threaded apartment; xUnit runs tests on MTA threads.
    static void RunOnStaThread(Action test)
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
