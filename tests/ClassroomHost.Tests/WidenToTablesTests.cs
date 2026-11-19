// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-170: a tool window opens wide enough for its table's columns.</summary>
public sealed class WidenToTablesTests
{
    static Window Stage(double width, UIElement content)
    {
        var window = new Window { Left = -20000, Top = -20000, Width = width, Height = 400, ShowInTaskbar = false, ShowActivated = false, Content = content };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    static ListView Table(params double[] columnWidths)
    {
        var grid = new GridView();
        foreach (var width in columnWidths)
            grid.Columns.Add(new GridViewColumn { Header = "H", Width = width, DisplayMemberBinding = new Binding("X") });
        var list = new ListView { View = grid, Margin = new Thickness(8) };
        list.Items.Add(new { X = "a" });
        return list;
    }

    [Fact]
    public void A_window_narrower_than_its_columns_is_widened_up_to_most_of_the_screen() => OnSta(() =>
    {
        var window = Stage(600, Table(300, 300, 300, 300));
        try
        {
            var limit = SystemParameters.WorkArea.Width * 0.95;

            Assert.True(Ui.WidenToTables(window));
            window.UpdateLayout();

            // All four columns, or as much as the screen allows.
            Assert.True(window.Width >= Math.Min(1200, limit) - 1, $"width {window.Width}");
            Assert.True(window.Width <= limit + 1);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void A_window_that_is_wide_enough_is_left_alone() => OnSta(() =>
    {
        var window = Stage(900, Table(200, 200, 200));
        try
        {
            Assert.False(Ui.WidenToTables(window));
            Assert.Equal(900, window.Width);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void A_window_is_never_made_narrower_than_it_opened() => OnSta(() =>
    {
        var window = Stage(1000, Table(100, 100));
        try
        {
            Assert.False(Ui.WidenToTables(window));
            Assert.Equal(1000, window.Width);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void A_window_that_sizes_itself_to_its_content_or_is_not_in_its_normal_state_is_not_touched() => OnSta(() =>
    {
        var sizing = Stage(600, Table(300, 300, 300, 300));
        sizing.SizeToContent = SizeToContent.Width;
        var maximized = Stage(600, Table(300, 300, 300, 300));
        maximized.WindowState = WindowState.Maximized;
        try
        {
            Assert.False(Ui.WidenToTables(sizing));
            Assert.False(Ui.WidenToTables(maximized));
        }
        finally { sizing.Close(); maximized.Close(); }
    });

    [Fact]
    public void A_window_without_a_table_needs_nothing() => OnSta(() =>
    {
        var window = Stage(400, new StackPanel { Children = { new Button { Content = "OK" } } });
        try
        {
            Assert.Equal(0, Ui.WidthNeeded(window));
            Assert.False(Ui.WidenToTables(window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void A_small_list_beside_other_controls_does_not_decide_the_width() => OnSta(() =>
    {
        var panel = new DockPanel();
        var small = Table(200, 200, 200);
        small.Width = 150;
        DockPanel.SetDock(small, Dock.Left);
        panel.Children.Add(small);
        panel.Children.Add(new TextBox());
        var window = Stage(900, panel);
        try
        {
            Assert.Equal(0, Ui.WidthNeeded(window));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void The_columns_of_a_data_grid_count_by_their_width_and_hidden_ones_do_not() => OnSta(() =>
    {
        var grid = new DataGrid { AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "A", Width = 200 });
        grid.Columns.Add(new DataGridTextColumn { Header = "B", Width = 300 });
        grid.Columns.Add(new DataGridTextColumn { Header = "C", Width = 400, Visibility = Visibility.Collapsed });
        grid.Columns.Add(new DataGridTextColumn { Header = "D", Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        // 200 + 300 + the least a star column is given (80).
        Assert.Equal(580, Ui.ColumnsWidth(grid));
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
