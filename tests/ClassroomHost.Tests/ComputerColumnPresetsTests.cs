// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ComputerColumnPresetsTests
{
    [Fact]
    public void Presets_change_only_the_pending_selection_and_keep_technical_columns_available()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                string[] columns = [.. ComputerColumnPresets.Teacher, "Address", "Protocol"];
                var original = new[] { "Computer", "Address" };
                var form = new FormDialog("Columns", null).MultiSelect("columns", "Visible columns", columns, original,
                    presets: [("Compact teacher view", ComputerColumnPresets.Teacher), ("Show all columns", columns)]);
                var panel = (StackPanel)((ScrollViewer)form.Content).Content;
                var list = panel.Children.OfType<ListBox>().Single();
                list.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(original, list.SelectedItems.Cast<string>());
                var buttons = panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToList();
                buttons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(ComputerColumnPresets.Teacher, list.SelectedItems.Cast<string>());
                Assert.Contains("Address", list.Items.Cast<string>());
                list.SelectedItems.Add("Address");
                Assert.Contains("Address", form.S("columns"));
                buttons[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(columns, list.SelectedItems.Cast<string>());
                Assert.Equal(new[] { "Computer", "Address" }, original);
                form.Close();
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join(); failure?.Throw();
    }

    [Fact]
    public void Compact_choices_survive_save_load_and_mode_changes()
    {
        var folder = Directory.CreateTempSubdirectory("classroom-columns-");
        try
        {
            var path = Path.Combine(folder.FullName, "host.json");
            var settings = new HostSettings { HiddenComputerColumns = ["Address", "Protocol"], HostMode = HostModes.Teacher };
            settings.Save(path);
            var loaded = HostSettings.Load(path);
            loaded.HostMode = HostModes.Admin;
            loaded.Save(path);
            Assert.Equal(settings.HiddenComputerColumns, HostSettings.Load(path).HiddenComputerColumns);
            loaded.HiddenComputerColumns.Clear(); // Explicit Show all remains an explicit empty list.
            loaded.Save(path);
            Assert.Empty(HostSettings.Load(path).HiddenComputerColumns);
        }
        finally { folder.Delete(true); }
    }
}
