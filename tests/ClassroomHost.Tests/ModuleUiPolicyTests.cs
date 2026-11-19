// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ModuleUiPolicyTests
{
    [Fact]
    public void Open_windows_refresh_restore_original_visibility_and_unsubscribe_on_close() => Sta(() =>
    {
        var settings = new HostSettings();
        var otherSettings = new HostSettings();
        var window = new Window();
        var button = new Button();
        var alreadyHidden = new Button { Visibility = Visibility.Collapsed };
        var otherWindow = new Window();
        var otherButton = new Button();
        ModuleUiPolicy.WatchVisibility(window, settings, BuiltInModules.Software, button, alreadyHidden);
        ModuleUiPolicy.WatchVisibility(otherWindow, otherSettings, BuiltInModules.Software, otherButton);
        var refreshes = 0;
        ModuleUiPolicy.Watch(window, settings, () => refreshes++);
        try
        {
            settings.DisabledModules = [BuiltInModules.Software];
            ModuleUiPolicy.Refresh(settings);
            Assert.Equal(Visibility.Collapsed, button.Visibility);
            Assert.Equal(Visibility.Visible, otherButton.Visibility);
            settings.DisabledModules.Clear();
            ModuleUiPolicy.Refresh(settings);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.Equal(Visibility.Collapsed, alreadyHidden.Visibility);
            window.Close();
            var before = refreshes;
            ModuleUiPolicy.Refresh(settings);
            Assert.Equal(before, refreshes);
        }
        finally { window.Close(); otherWindow.Close(); }
    });

    [Fact]
    public void Existing_schedule_window_hides_new_work_but_keeps_delete_and_saved_schedule() => Sta(() =>
    {
        var settings = new HostSettings { Schedules = [new() { Name = "Saved" }] };
        var window = new SchedulesWindow(settings, null!, () => [], () => []);
        try
        {
            var root = (DockPanel)window.Content;
            var buttons = root.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
            Assert.Equal(Visibility.Visible, buttons[0].Visibility);
            settings.DisabledModules = [BuiltInModules.Automation];
            ModuleUiPolicy.Refresh(settings);
            Assert.Equal(Visibility.Collapsed, buttons[0].Visibility);
            Assert.Equal(Visibility.Collapsed, buttons[1].Visibility);
            Assert.Equal(Visibility.Visible, buttons[2].Visibility); // Delete saved job remains available.
            Assert.Single(settings.Schedules);
            settings.DisabledModules.Clear();
            ModuleUiPolicy.Refresh(settings);
            Assert.Equal(Visibility.Visible, buttons[0].Visibility);
        }
        finally { window.Close(); }
    });

    static void Sta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = ExceptionDispatchInfo.Capture(exception); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        error?.Throw();
    }

    [Fact]
    public void Disabled_commands_and_schedule_choices_hide_and_reenable_without_erasing_preferences()
    {
        var settings = new HostSettings { DisabledModules = BuiltInModules.Available.Select(module => module.Id).ToList() };
        Assert.False(ModuleUiPolicy.CommandVisible(settings, "restrictions.inventory"));
        Assert.False(ModuleUiPolicy.CommandVisible(settings, "settings.schedules"));
        Assert.False(ModuleUiPolicy.CommandVisible(settings, "remote.programs", 2));
        Assert.True(ModuleUiPolicy.CommandVisible(settings, "remote.programs", 1));
        Assert.True(ModuleUiPolicy.CommandVisible(settings, "remote.windows-update", 2));
        Assert.True(ModuleUiPolicy.CommandVisible(settings, "classroom.screen-saver"));
        Assert.True(ModuleUiPolicy.CommandVisible(settings, "restrictions.browsers"));
        Assert.True(ModuleUiPolicy.CommandVisible(settings, "classroom.recent-files"));
        Assert.False(ModuleUiPolicy.ScheduleActionVisible(settings, "backup"));
        Assert.False(ModuleUiPolicy.ScheduleActionVisible(settings, "updatescan"));
        Assert.True(ModuleUiPolicy.ScheduleActionVisible(settings, "lock"));
        Assert.False(ModuleUiPolicy.ToolbarEntryVisible(settings, ToolbarArrangement.Id("Settings", "_Schedules...")));
        settings.DisabledModules.Clear();
        Assert.True(ModuleUiPolicy.CommandVisible(settings, "settings.schedules"));
        Assert.True(ModuleUiPolicy.CommandVisible(settings, "remote.programs", 2));
        Assert.True(ModuleUiPolicy.ScheduleActionVisible(settings, "backup"));
        Assert.True(ModuleUiPolicy.ScheduleActionVisible(settings, "updatescan"));
    }

    [Fact]
    public void Module_toggle_hides_dependent_settings_instantly_without_changing_their_values()
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            var form = new FormDialog("Settings", null).Check("schedule", "Scheduling", false)
                .Check("background", "Run when closed", true).Text("path", "Path", "saved")
                .ShowWhenChecked("schedule", "background", "path");
            try
            {
                var panel = (StackPanel)((ScrollViewer)form.Content).Content;
                var checks = panel.Children.OfType<CheckBox>().ToArray();
                var text = panel.Children.OfType<TextBox>().Single();
                Assert.Equal(Visibility.Visible, checks[0].Visibility);
                Assert.Equal(Visibility.Collapsed, checks[1].Visibility);
                Assert.Equal(Visibility.Collapsed, text.Visibility);
                Assert.Equal(Visibility.Collapsed, panel.Children.OfType<Label>().Single().Visibility);
                Assert.True(form.B("background"));
                Assert.Equal("saved", form.S("path"));
                checks[0].IsChecked = true;
                Assert.Equal(Visibility.Visible, checks[1].Visibility);
                Assert.Equal(Visibility.Visible, text.Visibility);
                checks[0].IsChecked = false;
                Assert.Equal(Visibility.Collapsed, text.Visibility);
                Assert.True(form.B("background"));
            }
            catch (Exception exception) { error = ExceptionDispatchInfo.Capture(exception); }
            finally { form.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        error?.Throw();
    }
}
