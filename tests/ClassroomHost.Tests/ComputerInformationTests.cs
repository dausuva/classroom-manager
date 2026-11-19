// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>T-083: the one-computer actions in Computer information, and lower buttons in dialogs.</summary>
public sealed class ComputerInformationTests
{
    [Fact]
    public void The_one_computer_actions_are_grouped_follow_the_mode_and_run_for_this_computer() => OnSta(() =>
    {
        var ran = new List<string>();
        var client = new ClientInfo { Name = "PC-07", Address = "192.0.2.7" };
        var window = new ClientInfoWindow(client, new HostSettings(), null!, label => label != "Registry...", ran.Add);
        var buttons = Descendants((DependencyObject)window.Content).OfType<Button>()
            .Select(button => button.Content as string ?? "").ToList();

        foreach (var label in ClientInfoWindow.ActionGroups.SelectMany(group => group.Actions).Where(label => label != "Registry..."))
            Assert.Contains(label, buttons);
        Assert.DoesNotContain("Registry...", buttons);   // hidden in this mode, so not offered here either

        var processes = Descendants((DependencyObject)window.Content).OfType<Button>().Single(button => (string)button.Content == "Processes...");
        processes.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(["Processes..."], ran);
    });

    [Fact]
    public void Dialog_buttons_are_lower_than_the_toolbar_ones_and_keep_the_rest_of_the_style() => OnSta(() =>
    {
        var window = new Window();
        foreach (var theme in new[] { "Light", "Controls" })
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/ClassroomHost;component/Themes/{theme}.xaml", UriKind.Relative) });
        var app = (Style)window.FindResource(typeof(Button));

        var dialog = new StackPanel { Children = { new Button { Content = "OK" } } };
        window.Content = dialog;
        Ui.CompactButtons(dialog);

        var compact = (Style)dialog.Resources[typeof(Button)];
        Assert.Same(app, compact.BasedOn);
        Assert.Contains(compact.Setters.OfType<Setter>(), setter => setter.Property == FrameworkElement.MinHeightProperty && (double)setter.Value < 36);
    });

    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
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
