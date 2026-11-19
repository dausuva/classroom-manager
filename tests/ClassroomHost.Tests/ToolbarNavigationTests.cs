// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ToolbarNavigationTests
{
    [Theory]
    [InlineData("SyncTime_Click", "Classroom", true)]
    [InlineData("Power_Click", "Computers", false)]
    [InlineData("CancelPower_Click", "Computers", false)]
    [InlineData("WindowsUpdate_Click", "Computers", false)]
    [InlineData("HideMessages_Click", "Classroom", false)]
    [InlineData("OpenWebsite_Click", "Classroom", false)]
    [InlineData("OpenProgram_Click", "Classroom", false)]
    [InlineData("Browsers_Click", "Software", false)]
    [InlineData("Languages_Click", "Settings", true)]
    [InlineData("Help_Click", "Help", false)]
    [InlineData("About_Click", "Help", false)]
    [InlineData("Licenses_Click", "Help", false)]
    [InlineData("Feedback_Click", "Help", false)]
    public void Commands_are_in_the_requested_group_and_row(string handler, string group, bool more)
    {
        var xaml = XDocument.Load(Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost", "MainWindow.xaml"));
        var button = Assert.Single(xaml.Descendants(), element => (string?)element.Attribute("Click") == handler);
        Assert.Equal(group, (string?)button.Ancestors().First(element => element.Name.LocalName == "TabItem").Attribute("Header"));
        Assert.Equal(more, button.Ancestors().Any(element => element.Name.LocalName == "Expander"));
    }
}
