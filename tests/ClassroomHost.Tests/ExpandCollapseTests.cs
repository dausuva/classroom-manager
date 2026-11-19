// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ExpandCollapseTests
{
    [Fact]
    public void Expand_all_and_collapse_all_open_and_close_every_computer_branch()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var tree = new TreeView();
                for (int i = 0; i < 3; i++)
                {
                    var branch = new TreeViewItem { Header = $"PC-{i}", IsExpanded = i == 1 };
                    branch.Items.Add(new TreeViewItem { Header = "entry" });
                    tree.Items.Add(branch);
                }
                var (expand, collapse) = Ui.ExpandCollapseButtons(tree);
                Assert.Equal("E_xpand all", expand.Content);
                Assert.Equal("Colla_pse all", collapse.Content);

                Ui.SetExpanded(tree, true);
                Assert.All(tree.Items.Cast<TreeViewItem>(), branch => Assert.True(branch.IsExpanded));
                Ui.SetExpanded(tree, false);
                Assert.All(tree.Items.Cast<TreeViewItem>(), branch => Assert.False(branch.IsExpanded));
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
