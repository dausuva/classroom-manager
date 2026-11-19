// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class OperationsLayoutTests
{
    static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// T-085: with 20 computers and several operations, the computers of an operation were row details inside the
    /// operations table, cut off by its height and scrolled with it. They have their own table beside it.
    /// </summary>
    [Fact]
    public void The_computers_of_an_operation_have_their_own_table_beside_the_operations()
    {
        var xaml = XDocument.Load(Path.Combine(SourceRoot(), "src", "ClassroomHost", "MainWindow.xaml"));
        XElement Named(string name) => xaml.Descendants().Single(element => (string?)element.Attribute(X + "Name") == name);

        var operations = Named("OperationsGrid");
        Assert.Empty(operations.Descendants(Wpf + "DataGrid.RowDetailsTemplate"));
        Assert.Empty(operations.Descendants(Wpf + "ScrollViewer"));

        var results = Named("OperationResultsGrid");
        Assert.Equal("{Binding SelectedItem.Results, ElementName=OperationsGrid}", (string?)results.Attribute("ItemsSource"));
        // Side by side in one grid, not one inside the other.
        Assert.Same(operations.Parent, Named("OperationResultsPanel").Parent);
    }

    static string SourceRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "ClassroomManager.sln"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("The repository folder was not found.");
    }
}
