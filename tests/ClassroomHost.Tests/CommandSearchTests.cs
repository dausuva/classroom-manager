// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

[Collection(nameof(LanguageSwitching))]
public sealed class CommandSearchTests
{
    [Theory]
    [InlineData("")]
    [InlineData("p")]
    [InlineData(" p ")]
    public void Fewer_than_two_characters_returns_no_matches(string query) =>
        Assert.Empty(CommandSearch.Find(["Programs..."], query));

    [Fact]
    public void Matches_are_alphabetical_unique_and_identify_the_substring()
    {
        var matches = CommandSearch.Find(["Unlock", "Lock screen...", "Block apps...", "lock screen...", "Programs..."], "  LOCK ");
        Assert.Equal(["Block apps...", "Lock screen...", "Unlock"], matches.Select(match => match.Label));
        Assert.Equal([1, 0, 2], matches.Select(match => match.Start));
        Assert.All(matches, match => Assert.Equal(4, match.Length));
    }

    [Fact]
    public void Search_respects_menu_visibility_selection_and_live_execution_state()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            var language = Loc.Current.Language;
            try
            {
                Loc.Use(Loc.English, UiTranslator.LanguagesFolder);
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var theme in new[] { "Light", "Controls" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/ClassroomHost;component/Themes/{theme}.xaml", UriKind.Relative) });
                window = new MainWindow(); // Never shown: no discovery, schedules or Client connections.
                object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                void Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
                var settings = (HostSettings)Field("_settings");
                settings.HostMode = HostModes.Admin;
                Call("ApplyHostMode");
                var box = (CommandSearchTextBox)window.FindName("CommandSearchBox");
                var results = (ListBox)window.FindName("CommandSearchResults");
                // Assistive technology can discover the relationship before the first query.
                var searchPeer = UIElementAutomationPeer.CreatePeerForElement(box)!;
                Assert.Same(UIElementAutomationPeer.CreatePeerForElement(results), Assert.Single(searchPeer.GetControlledPeers()));
                Button Activate(ListBoxItem item) => ((Grid)item.Content).Children.OfType<Button>().First();
                Button Reveal(ListBoxItem item) => ((Grid)item.Content).Children.OfType<Button>().Last();
                var searchPanel = (FrameworkElement)window.FindName("CommandSearchPanel");
                var tabs = (TabControl)window.FindName("MainTabs");
                settings.QuickActions = ["lock", "send-files"];
                settings.CustomQuickActions.Clear();
                Call("BuildQuickActions");
                var customActions = (Panel)window.FindName("QuickActionsPanel");
                Assert.Equal(Visibility.Collapsed, customActions.Visibility);
                var quickButtons = (Dictionary<string, Button>)Field("_builtInQuickButtons");
                Assert.Equal(["lock", "send-files"], quickButtons.Where(pair => pair.Value.Visibility == Visibility.Visible).Select(pair => pair.Key));
                Assert.False(quickButtons.ContainsKey("wake"));
                settings.CustomQuickActions.Add(new CustomQuickAction { Name = "Homework", Kind = CustomQuickActionKinds.Message });
                Call("BuildQuickActions");
                Assert.Equal(Visibility.Visible, customActions.Visibility);
                Assert.Equal("Homework", Assert.Single(customActions.Children.OfType<Button>()).Content);
                var quickPanel = (Panel)window.FindName("BuiltInQuickActionsPanel");
                Assert.DoesNotContain(quickPanel.Children.OfType<Button>(), button => (button.Content as string ?? "").Replace("_", "") == "Refresh status");
                var content = (FrameworkElement)window.Content;
                foreach (var width in new[] { 1250d, 720d })
                {
                    content.Measure(new Size(width, 720));
                    content.Arrange(new Rect(0, 0, width, 720));
                    content.UpdateLayout();
                    var searchBounds = new Rect(searchPanel.TranslatePoint(new Point(), content), searchPanel.RenderSize);
                    var headers = tabs.Items.Cast<TabItem>().Where(tab => tab.Visibility == Visibility.Visible).ToList();
                    Assert.InRange(Math.Abs(headers[0].TranslatePoint(new Point(), content).Y - searchBounds.Top), 0, 2);
                    foreach (var tab in headers)
                    {
                        var tabBounds = new Rect(tab.TranslatePoint(new Point(), content), tab.RenderSize);
                        Assert.False(tabBounds.IntersectsWith(searchBounds), $"Search overlaps {tab.Header} at width {width}");
                    }
                }
                // Real toolbar overflow is temporary, searchable, and revealable even when disabled.
                Call("RestoreToolbarOverflow");
                var computerTab = tabs.Items.Cast<TabItem>().Single(tab => (string)tab.Header == "Computers");
                var computerStack = (StackPanel)computerTab.Content;
                var computerTop = computerStack.Children.OfType<WrapPanel>().First();
                var fullOrder = computerTop.Children.Cast<UIElement>().ToArray();
                var savedLayout = System.Text.Json.JsonSerializer.Serialize(settings.ToolbarLayouts);
                var computerMore = computerStack.Children.OfType<Expander>().Single();
                var overflow = ((StackPanel)((ScrollViewer)computerMore.Content).Content).Children.OfType<WrapPanel>().First();
                tabs.SelectedItem = computerTab;
                content.Measure(new Size(720, 720));
                content.Arrange(new Rect(0, 0, 720, 720));
                content.UpdateLayout();
                Call("RefreshToolbarOverflow");
                Assert.NotEmpty(overflow.Children.OfType<Button>());
                Assert.True(computerTop.Children.OfType<UIElement>().Where(element => element.Visibility == Visibility.Visible)
                    .Sum(element => element.DesiredSize.Width) <= computerTop.ActualWidth + 0.5);
                var overflowCommand = overflow.Children.OfType<Button>().First(button => !button.IsEnabled);
                box.Text = UiTranslator.Source((string)overflowCommand.Content).Replace("_", "");
                var overflowResult = Assert.Single(results.Items.Cast<ListBoxItem>(), item => ReferenceEquals(item.Tag, overflowCommand));
                Assert.Contains("Computers › More ›", ((StackPanel)Activate(overflowResult).Content).Children.OfType<TextBlock>().ElementAt(1).Text);
                Reveal(overflowResult).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(computerMore.IsExpanded);
                content.Measure(new Size(3000, 720));
                content.Arrange(new Rect(0, 0, 3000, 720));
                content.UpdateLayout();
                Call("RefreshToolbarOverflow");
                Assert.Empty(overflow.Children);
                Assert.Equal(fullOrder, computerTop.Children.Cast<UIElement>());
                Assert.Equal(savedLayout, System.Text.Json.JsonSerializer.Serialize(settings.ToolbarLayouts));
                computerMore.IsExpanded = false;
                tabs.SelectedItem = tabs.Items[0];
                box.Text = "terminal";
                var terminal = Assert.Single(results.Items.Cast<ListBoxItem>());
                Assert.Same(UIElementAutomationPeer.CreatePeerForElement(results), Assert.Single(searchPeer.GetControlledPeers()));
                Assert.Contains("Matching commands: 1", searchPeer.GetHelpText());
                Assert.NotNull(searchPeer.GetPattern(PatternInterface.Value));
                Assert.True(terminal.IsEnabled); // Disabled commands can still be located without running them.
                Assert.False(Activate(terminal).IsEnabled);
                Assert.True(Reveal(terminal).IsEnabled);
                Assert.Contains("Software", AutomationProperties.GetName(Activate(terminal)));
                Assert.Contains("Select exactly one computer.", AutomationProperties.GetHelpText(Activate(terminal)));
                var highlighted = ((StackPanel)Activate(terminal).Content).Children.OfType<TextBlock>().First().Inlines.OfType<Run>().Single(run => run.FontWeight == FontWeights.Bold);
                Assert.Equal("Terminal", highlighted.Text);
                Assert.Contains("Software ›", ((StackPanel)Activate(terminal).Content).Children.OfType<TextBlock>().ElementAt(1).Text);
                Loc.Use(Loc.Lithuanian, UiTranslator.LanguagesFolder);
                Call("UpdateCommandSearch");
                var translated = Assert.Single(results.Items.Cast<ListBoxItem>());
                Assert.Contains("Programinė įranga ›", ((StackPanel)Activate(translated).Content).Children.OfType<TextBlock>().ElementAt(1).Text);
                Assert.Contains("Atitinkančių komandų: 1", searchPeer.GetHelpText());
                Assert.DoesNotContain("Type at least two letters", searchPeer.GetHelpText());
                Loc.Use(Loc.English, UiTranslator.LanguagesFolder);
                Call("UpdateCommandSearch");
                var clients = (ObservableCollection<ClientInfo>)Field("_clients");
                clients.Clear();
                Call("UpdateCollectionState");
                Assert.False(quickButtons["lock"].IsEnabled);
                Assert.False(Assert.Single(customActions.Children.OfType<Button>()).IsEnabled);
                Assert.Contains("Select exactly one computer.", terminal.ToolTip?.ToString());
                clients.Add(new ClientInfo { Name = "Test", Address = "test.invalid", Room = "Test", IsSelected = true });
                Call("UpdateCollectionState");
                Assert.True(quickButtons["lock"].IsEnabled);
                Assert.True(Assert.Single(customActions.Children.OfType<Button>()).IsEnabled);
                Call("UpdateCommandSearch");
                Assert.True(Activate(Assert.Single(results.Items.Cast<ListBoxItem>())).IsEnabled);
                settings.HostMode = HostModes.Teacher;
                Call("ApplyHostMode");
                Call("UpdateCommandSearch");
                Assert.Empty(results.Items);
                Assert.Equal("No matching commands", box.SuggestionStatus);
                settings.HostMode = HostModes.Admin;
                settings.ToolbarLayouts["Software"] = new ToolbarLayout { Hidden = ["Software|Terminal..."] };
                Call("ApplyToolbarLayout");
                Call("ApplyHostMode");
                Call("UpdateCommandSearch");
                Assert.Empty(results.Items);

                // A harmless local action exercises the same click path as real commands, including toggles.
                Call("RestoreToolbarOverflow");
                var top = ((StackPanel)((TabItem)tabs.Items[0]).Content).Children.OfType<WrapPanel>().First();
                int clicks = 0;
                var command = new ToggleButton { Content = "Test action" };
                command.Click += (_, _) => clicks++;
                top.Children.Add(command);
                Call("CaptureToolbarOverflow");
                box.Text = "test action";
                var result = Assert.Single(results.Items.Cast<ListBoxItem>());
                var activate = Activate(result);
                Reveal(result).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(0, clicks);
                Assert.Same(tabs.Items[0], tabs.SelectedItem);
                // Paths follow the actual toolbar location after moving to More.
                var firstTabContent = (StackPanel)((TabItem)tabs.Items[0]).Content;
                var more = firstTabContent.Children.OfType<Expander>().First();
                var morePanel = ((StackPanel)((ScrollViewer)more.Content).Content).Children.OfType<WrapPanel>().Last();
                Call("RestoreToolbarOverflow");
                top.Children.Remove(command);
                morePanel.Children.Add(command);
                Call("CaptureToolbarOverflow");
                Call("UpdateCommandSearch");
                result = Assert.Single(results.Items.Cast<ListBoxItem>());
                Assert.Contains("› More ›", ((StackPanel)Activate(result).Content).Children.OfType<TextBlock>().ElementAt(1).Text);
                Reveal(result).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.True(more.IsExpanded);
                Assert.Equal(0, clicks);
                Call("RestoreToolbarOverflow");
                morePanel.Children.Remove(command);
                top.Children.Add(command);
                Call("CaptureToolbarOverflow");
                Call("UpdateCommandSearch");
                activate = Activate(Assert.Single(results.Items.Cast<ListBoxItem>()));
                command.IsEnabled = false;
                activate.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(0, clicks); // Live availability is checked again, even for an old result.
                command.IsEnabled = true;
                activate.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, clicks);
                Assert.True(command.IsChecked);
                Assert.Equal("", box.Text);

                int otherClicks = 0;
                var otherCommand = new Button { Content = "Test action two" };
                otherCommand.Click += (_, _) => otherClicks++;
                Call("RestoreToolbarOverflow");
                top.Children.Add(otherCommand);
                Call("CaptureToolbarOverflow");
                box.Text = "test action";
                Assert.Equal(2, results.Items.Count);
                // Clicking a different row must activate that row, even while the first is selected.
                Assert.Same(results.Items[0], results.SelectedItem);
                Activate((ListBoxItem)results.Items[1]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, otherClicks);
                Assert.Equal(1, clicks);

                var scan = (Button)window.FindName("ScanButton");
                var shortcut = (Button)window.FindName("ListScanButton");
                scan.IsEnabled = false;
                Assert.False(shortcut.IsEnabled);
                scan.IsEnabled = true;
                Assert.True(shortcut.IsEnabled);
                Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("OperationsBadge")).Visibility);
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally
            {
                window?.Close();
                Loc.Use(language, UiTranslator.LanguagesFolder);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
