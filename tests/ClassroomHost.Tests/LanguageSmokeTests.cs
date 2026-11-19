// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// T-179: what the Lithuanian screen of every tool window must not do. The Columns... crash of T-176 was found
/// only by clicking; this builds each window in the program's second language and looks for the same kind of
/// trouble: a window that cannot be built, laid out or translated, and two different English texts that read
/// the same in Lithuanian, which a program that finds an English name from its translation confuses.
/// </summary>
[Collection(nameof(LanguageSwitching))]
public sealed class LanguageSmokeTests
{
    /// <summary>Pairs of table columns whose English titles read the same in Lithuanian; they are told apart by the English name each column remembers.</summary>
    static readonly HashSet<string> KnownAlikeColumns = ["State|Status"];

    sealed record Findings(int Windows, List<string> Problems, List<string> Alike);

    [Fact]
    public void Every_tool_window_builds_translates_and_lays_out_in_Lithuanian_and_its_columns_keep_their_own_names()
    {
        var found = OnSta(Run);

        Assert.True(found.Windows >= 15, $"Only {found.Windows} window(s) could be built.");
        Assert.True(found.Problems.Count == 0, "In Lithuanian:\n" + string.Join("\n", found.Problems));
    }

    [Fact]
    public void Texts_that_read_alike_in_Lithuanian_are_listed_so_a_new_one_is_a_decision()
    {
        var found = OnSta(Run);

        // Not a failure: two buttons that read the same are only a problem to the person using them. A new
        // pair appearing is what this test is for; add it to the list below once it is judged harmless.
        var unexpected = found.Alike.Except(KnownAlikeButtons).ToList();
        Assert.True(unexpected.Count == 0,
            "Different English texts that read the same in one Lithuanian window (judge each; add harmless ones to KnownAlikeButtons):\n" + string.Join("\n", unexpected));
    }

    [Fact]
    public void The_check_notices_two_columns_that_read_alike_unless_the_pair_is_known() => OnSta(() =>
    {
        var previous = Loc.Current;
        Loc.Use(Loc.BuiltIn(Loc.Lithuanian));
        var window = new Window { Left = -20000, Top = -20000, Width = 600, Height = 300, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            var grid = new DataGrid { AutoGenerateColumns = false };
            foreach (var title in new[] { "State", "Status", "Computer", "Computer" })
                grid.Columns.Add(new DataGridTextColumn { Header = title, Width = 100 });
            window.Content = grid;
            window.Show();
            window.UpdateLayout();
            UiTranslator.TranslateTree(grid);

            var strict = Columns(grid, new HashSet<string>()).ToList();
            Assert.Contains("columns State and Status both show '" + Loc.T("State") + "'", strict);
            Assert.Contains("two columns are both called 'Computer'", strict);

            var known = Columns(grid, KnownAlikeColumns).ToList();
            Assert.DoesNotContain(known, problem => problem.Contains("State and Status"));
            Assert.Contains("two columns are both called 'Computer'", known);
        }
        finally { window.Close(); Loc.Use(previous); }
        return 0;
    });

    [Fact]
    public void The_check_notices_two_buttons_that_read_alike() => OnSta(() =>
    {
        var previous = Loc.Current;
        Loc.Use(Loc.BuiltIn(Loc.Lithuanian));
        try
        {
            Assert.Equal(["State | Status"], AlikeButtons(["State", "_Status", "Computer", "Computer"]).ToList());
            Assert.Empty(AlikeButtons(["Computer", "Room", "Close"]));
        }
        finally { Loc.Use(previous); }
        return 0;
    });

    /// <summary>Button pairs, as "Window: English one | English two", that were judged harmless.</summary>
    static readonly HashSet<string> KnownAlikeButtons = [];

    static Findings Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!app.Resources.MergedDictionaries.Any(dictionary => dictionary.Source?.OriginalString.Contains("Themes/Controls") == true))
            foreach (var theme in new[] { "Light", "Controls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/ClassroomHost;component/Themes/{theme}.xaml", UriKind.Relative) });

        var previous = Loc.Current;
        var problems = new List<string>();
        var alike = new List<string>();
        var built = 0;
        var stage = new Window { Left = -20000, Top = -20000, Width = 1100, Height = 750, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        stage.Show();
        try
        {
            UiTranslator.Initialize(Loc.Lithuanian);
            foreach (var type in typeof(MainWindow).Assembly.GetTypes()
                         .Where(type => typeof(Window).IsAssignableFrom(type) && !type.IsAbstract && !ButtonSpacingTests.NotBuiltHere.Contains(type.Name))
                         .OrderBy(type => type.Name))
            {
                Window window;
                try { window = ButtonSpacingTests.Build(type, stage); }
                catch (Exception) { continue; }   // the English audit counts the windows it cannot build
                if (window.Content is not FrameworkElement content) continue;

                try
                {
                    window.Content = null;
                    stage.Resources = window.Resources;
                    Ui.CompactButtons(stage);
                    stage.DataContext = window.DataContext;
                    stage.Width = double.IsNaN(window.Width) ? 1100 : Math.Max(window.Width, window.MinWidth);
                    stage.Height = double.IsNaN(window.Height) ? 750 : Math.Max(window.Height, window.MinHeight);
                    stage.Content = content;
                    stage.UpdateLayout();
                    // The English texts first: this is what the person's screen is translated from.
                    var english = Buttons(content).Select(button => (button, text: (string)button.Content)).ToList();
                    UiTranslator.TranslateTree(stage);
                    stage.UpdateLayout();
                    built++;

                    foreach (var table in Descendants(content).OfType<DataGrid>())
                        problems.AddRange(Columns(table, KnownAlikeColumns).Select(problem => $"{type.Name}: {problem}"));
                    foreach (var pair in AlikeButtons(english.Select(item => item.text)))
                        alike.Add($"{type.Name}: {pair}");
                }
                catch (Exception ex) { problems.Add($"{type.Name}: {ex.GetType().Name}: {ex.Message}"); }
                finally { stage.Content = null; }
            }
        }
        finally { stage.Close(); Loc.Use(previous); }
        return new Findings(built, problems, alike);
    }

    /// <summary>One problem per pair of columns of a table that share an English name, or that read alike without being a known pair.</summary>
    static IEnumerable<string> Columns(DataGrid grid, ISet<string> knownAlike)
    {
        var named = TableColumns.Named(grid);
        foreach (var group in named.GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            yield return $"two columns are both called '{group.Key}'";
        foreach (var group in named.GroupBy(column => (string)column.Column.Header).Where(group => group.Select(column => column.Name).Distinct().Count() > 1))
        {
            var names = string.Join("|", group.Select(column => column.Name).Distinct().Order());
            if (!knownAlike.Contains(names)) yield return $"columns {names.Replace("|", " and ")} both show '{group.Key}'";
        }
    }

    static IEnumerable<string> AlikeButtons(IEnumerable<string> englishTexts) =>
        englishTexts.Select(text => text.Replace("_", "").Trim()).Where(text => text.Length > 0).Distinct()
            .GroupBy(text => Loc.T(text).Replace("_", "").Trim())
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(" | ", group.Order()));

    static IEnumerable<Button> Buttons(DependencyObject root) =>
        Descendants(root).OfType<Button>().Where(button => button.Content is string text && text.Length > 0);

    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    static T OnSta<T>(Func<T> test)
    {
        ExceptionDispatchInfo? failure = null;
        T result = default!;
        var thread = new Thread(() => { try { result = test(); } catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
