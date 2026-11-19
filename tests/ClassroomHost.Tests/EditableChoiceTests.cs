// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class EditableChoiceTests
{
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Themed_choice_displays_defaults_selection_and_custom_input(string theme)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            var dialog = new FormDialog("Test", null)
                .Choice("room", "Room", [("Lab 1", "First room"), ("Lab 2", "Second room")], "Saved room")
                .Folder("folder", "Folder", @"C:\Saved");
            try
            {
                dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/ClassroomHost;component/Themes/{theme}.xaml", UriKind.Relative) });
                dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ClassroomHost;component/Themes/Controls.xaml", UriKind.Relative) });
                var panel = (StackPanel)((ScrollViewer)dialog.Content).Content;
                var combo = panel.Children.OfType<ComboBox>().Single();
                combo.ApplyTemplate();
                var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
                Assert.Equal(Visibility.Visible, editor.Visibility);
                Assert.Equal("Saved room", editor.Text);
                combo.SelectedIndex = 1;
                Assert.Equal("Lab 2", editor.Text);
                Assert.Equal("Lab 2", dialog.S("room"));
                editor.Text = "New room";
                Assert.Equal("New room", dialog.S("room"));
                var folder = panel.Children.OfType<DockPanel>().Single().Children.OfType<ComboBox>().Single();
                folder.ApplyTemplate();
                Assert.Equal(@"C:\Saved", Assert.IsType<TextBox>(folder.Template.FindName("PART_EditableTextBox", folder)).Text);
            }
            catch (Exception exception) { error = ExceptionDispatchInfo.Capture(exception); }
            finally { dialog.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        error?.Throw();
    }

    [Fact]
    public void New_folder_is_created_and_existing_content_is_never_replaced()
    {
        var root = Directory.CreateTempSubdirectory("folder-create-").FullName;
        try
        {
            var path = LocalFolderCreation.Create(root, "New folder");
            Assert.True(Directory.Exists(path));
            Assert.Equal(Path.Combine(root, "New folder"), path);
            Assert.Throws<IOException>(() => LocalFolderCreation.Create(root, "New folder"));
            Assert.Throws<IOException>(() => LocalFolderCreation.Create(root, "..\\escape"));
            Assert.Throws<IOException>(() => LocalFolderCreation.Create(root, ".."));
            Assert.Throws<IOException>(() => LocalFolderCreation.Create(Path.Combine(root, "missing"), "child"));
        }
        finally { Directory.Delete(root, true); }
    }
}
