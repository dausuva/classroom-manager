// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// The Host window builds, and every named control of <c>MainWindow.xaml</c> is there. It is the first check after code is moved between the
/// <c>MainWindow</c> files (docs/MAINWINDOW-SPLIT-PLAN.md). The window is never shown; settings come from the test's scratch folder.
/// (A missing event handler cannot get this far: the XAML compiler refuses it.)
/// </summary>
[Collection(nameof(LanguageSwitching))]
public sealed class MainWindowSmokeTests
{
    [Fact]
    public void The_window_builds_and_every_named_control_of_the_XAML_is_found()
    {
        var names = Regex.Matches(File.ReadAllText(Path.Combine(MainWindowSource.Folder, "MainWindow.xaml")), @"x:Name=""(\w+)""")
            .Select(match => match.Groups[1].Value).Distinct().ToList();
        Assert.True(names.Count > 20, "The XAML names fewer controls than expected; has the pattern changed?");

        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var language = Loc.Current.Language;
            try
            {
                Loc.Use(Loc.English, UiTranslator.LanguagesFolder);
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var theme in new[] { "Light", "Controls" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/ClassroomHost;component/Themes/{theme}.xaml", UriKind.Relative) });
                var window = new MainWindow();
                var missing = names.Where(name => window.FindName(name) is null).ToList();
                Assert.True(missing.Count == 0, "Not found in the built window: " + string.Join(", ", missing));
                Assert.True(((TabControl)window.FindName("MainTabs")).Items.Count >= 5);
                Assert.False(string.IsNullOrWhiteSpace(window.Title));
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { Loc.Use(language, UiTranslator.LanguagesFolder); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
