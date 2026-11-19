// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class CollectFolderFieldTests
{
    /// <summary>Editing a sync quick action emptied its "Sync into" folder: the field started blank.</summary>
    [Fact]
    public void A_folder_field_starts_with_the_saved_folder_and_offers_the_usual_ones()
    {
        OnSta(() =>
        {
            var dlg = new FormDialog("Sync", null)
                .Folder("local", "Sync into", @"D:\Homework", CollectFolders.OnThisComputer([@"D:\Homework", @"E:\Older"]));
            Assert.Equal(@"D:\Homework", dlg.S("local"));

            var combo = Find<ComboBox>(dlg);
            Assert.True(combo.IsEditable);
            // Recent folders first, once each, then the teacher's own Documents and Desktop.
            Assert.Equal(4, combo.Items.Count);
        });
    }

    [Fact]
    public void Picking_a_student_folder_puts_its_token_in_the_field_and_a_typed_path_is_kept()
    {
        OnSta(() =>
        {
            var dlg = new FormDialog("Collect", null).Choice("folder", "Folder", CollectFolders.OnStudentComputer, "%DOCUMENTS%");
            Assert.Equal("%DOCUMENTS%", dlg.S("folder"));

            var combo = Find<ComboBox>(dlg);
            combo.SelectedIndex = 0;
            Assert.Equal("%DESKTOP%", dlg.S("folder"));

            combo.Text = @"C:\Exams\Week 3";
            Assert.Equal(@"C:\Exams\Week 3", dlg.S("folder"));
        });
    }

    /// <summary>T-088: what a person sees in the list has to be the folder's name, and picking it fills the field.</summary>
    [Fact]
    public void The_open_list_shows_each_folder_by_name_and_picking_one_fills_the_field()
    {
        OnSta(() =>
        {
            var dlg = new FormDialog("Send files", null).Choice("dest", "Put the files in", CollectFolders.OnStudentComputer, "%DESKTOP%");
            dlg.Left = -10000;
            dlg.ShowActivated = false;
            dlg.Show();
            try
            {
                var combo = Find<ComboBox>(dlg);
                combo.IsDropDownOpen = true;
                dlg.UpdateLayout();
                var shown = combo.Items.Cast<object>()
                    .Select(item => combo.ItemContainerGenerator.ContainerFromItem(item) as ComboBoxItem)
                    .Select(container => container is null ? "" : string.Concat(Texts(container)))
                    .ToList();
                Assert.Equal(CollectFolders.OnStudentComputer.Count, shown.Count);
                Assert.Contains("Documents", shown[1]);
                Assert.Contains("%DOCUMENTS%", shown[1]);

                combo.SelectedIndex = 2;
                combo.IsDropDownOpen = false;
                Assert.Equal("%DOWNLOADS%", dlg.S("dest"));
            }
            finally { dlg.Close(); }
        });
    }

    static IEnumerable<string> Texts(System.Windows.DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock { Text.Length: > 0 } text) yield return text.Text;
            foreach (var deeper in Texts(child)) yield return deeper;
        }
    }

    static T Find<T>(FormDialog dlg) where T : class
    {
        var pending = new Stack<object>([dlg.Content]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is T found) return found;
            if (current is ScrollViewer { Content: { } content }) pending.Push(content);
            if (current is Panel panel) foreach (var child in panel.Children) pending.Push(child);
        }
        throw new InvalidOperationException($"No {typeof(T).Name} in the dialog.");
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
