// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public partial class MainWindow
{
    // Use local visibility rather than IsVisible: commands in an unselected tab or a closed More
    // section are searchable. Detached actions were hidden by Arrange toolbar and stay out.
    IEnumerable<ButtonBase> SearchableCommands() => _toolbarTabs.SelectMany(tab =>
        tab.Top.Children.Cast<UIElement>().Concat(tab.More.Children.Cast<UIElement>()).Concat(tab.Overflow.Children.Cast<UIElement>()))
        .OfType<ButtonBase>().Where(button => button.Visibility == Visibility.Visible && button.Content is string);

    static string SearchLabel(ButtonBase button) => ButtonNameConverter.Clean(Loc.T(UiTranslator.Source(button.Content as string ?? "")));

    ToolbarTab? CommandTab(ButtonBase command) => _toolbarTabs.FirstOrDefault(tab =>
        tab.Top.Children.Contains(command) || tab.More.Children.Contains(command) || tab.Overflow.Children.Contains(command));

    string CommandPath(ButtonBase command)
    {
        var tab = CommandTab(command);
        if (tab is null) return SearchLabel(command);
        return Loc.T(tab.Name) + " › " + (tab.More.Children.Contains(command) || tab.Overflow.Children.Contains(command) ? Loc.T("More") + " › " : "") + SearchLabel(command);
    }

    void RevealSearchResult(ListBoxItem? item)
    {
        if (item?.Tag is not ButtonBase command || !SearchableCommands().Contains(command) || CommandTab(command) is not { } tab) return;
        CommandSearchPopup.IsOpen = false;
        MainTabs.SelectedItem = tab.Item;
        MainTabs.UpdateLayout();
        RefreshToolbarOverflow();
        if (tab.More.Children.Contains(command) || tab.Overflow.Children.Contains(command)) tab.MoreSection.IsExpanded = true;
        MainTabs.UpdateLayout();
        command.BringIntoView();
        // A disabled command cannot receive focus; focus its tab instead.
        if (!command.Focus()) tab.Item.Focus();
    }

    void CommandSearch_TextChanged(object sender, TextChangedEventArgs e) => UpdateCommandSearch();

    void UpdateCommandSearch()
    {
        if (CommandSearchResults is null) return;
        CommandSearchBox.Suggestions = CommandSearchResults;
        CommandSearchResults.Items.Clear();
        if (CommandSearchBox.Text.Trim().Length < 2)
        {
            CommandSearchPopup.IsOpen = false;
            CommandSearchBox.UpdateSuggestionStatus("", false);
            return;
        }
        var commands = SearchableCommands().GroupBy(SearchLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(button => CommandTab(button)?.Name == "Overview")
                .ThenByDescending(button => button.IsEnabled).First(), StringComparer.CurrentCultureIgnoreCase);
        foreach (var match in CommandSearch.Find(commands.Keys, CommandSearchBox.Text))
        {
            var command = commands[match.Label];
            var label = new TextBlock();
            label.Inlines.Add(new Run(match.Label[..match.Start]));
            label.Inlines.Add(new Run(match.Label.Substring(match.Start, match.Length))
            {
                FontWeight = FontWeights.Bold,
                TextDecorations = TextDecorations.Underline,
            });
            label.Inlines.Add(new Run(match.Label[(match.Start + match.Length)..]));
            var reason = CommandState.GetDisabledReason(command);
            var content = new StackPanel();
            content.Children.Add(label);
            var path = CommandPath(command);
            var pathText = Ui.MutedText(path, new Thickness(0, 3, 0, 0));
            pathText.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(pathText);
            if (reason.Length > 0) content.Children.Add(Ui.MutedText(Loc.T(reason), new Thickness(0, 3, 0, 0)));
            var item = new ListBoxItem
            {
                Tag = command,
                Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = command.ToolTip,
            };
            var activate = new Button
            {
                Content = content, IsEnabled = command.IsEnabled,
                Style = (Style)CommandSearchResults.FindResource("CommandSearchResultStyle"),
            };
            activate.Click += (_, e) =>
            {
                e.Handled = true;
                RunSearchResult(item);
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(activate);
            var reveal = new Button
            {
                Content = Loc.T("Show"), ToolTip = Loc.T("Show in toolbar (Ctrl+Enter)"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4),
            };
            reveal.Click += (_, e) => { e.Handled = true; RevealSearchResult(item); };
            Grid.SetColumn(reveal, 1);
            row.Children.Add(reveal);
            item.Content = row;
            ToolTipService.SetShowOnDisabled(item, true);
            ToolTipService.SetShowOnDisabled(activate, true);
            activate.ToolTip = command.ToolTip;
            System.Windows.Automation.AutomationProperties.SetHelpText(item, Loc.T(reason));
            System.Windows.Automation.AutomationProperties.SetName(activate, path);
            System.Windows.Automation.AutomationProperties.SetHelpText(activate, Loc.T(reason));
            System.Windows.Automation.AutomationProperties.SetName(reveal, Loc.T("Show in toolbar") + ": " + path);
            System.Windows.Automation.AutomationProperties.SetName(item, path);
            CommandSearchResults.Items.Add(item);
        }
        CommandSearchEmpty.Visibility = CommandSearchResults.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CommandSearchResults.SelectedItem = CommandSearchResults.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.IsEnabled);
        // Selection/mode updates must not reopen a dismissed popup.
        if (CommandSearchBox.IsKeyboardFocusWithin || CommandSearchPopup.IsOpen)
            CommandSearchPopup.IsOpen = true;
        var status = CommandSearchResults.Items.Count == 0 ? Loc.T("No matching commands") :
            Loc.F($"Matching commands: {CommandSearchResults.Items.Count}. Down selects a result; Enter runs it; Ctrl+Enter shows its toolbar location.");
        CommandSearchBox.UpdateSuggestionStatus(status, CommandSearchPopup.IsOpen);
    }

    void CommandSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            UpdateCommandSearch();
            if (CommandSearchPopup.IsOpen && CommandSearchResults.SelectedItem is ListBoxItem item) item.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ActivateSearchSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommandSearchPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    void CommandSearchResults_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ActivateSearchSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommandSearchPopup.IsOpen = false;
            CommandSearchBox.Focus();
            e.Handled = true;
        }
    }

    void ActivateSearchSelection()
    {
        var item = Keyboard.FocusedElement is DependencyObject focused
            ? ItemsControl.ContainerFromElement(CommandSearchResults, focused) as ListBoxItem : null;
        item ??= CommandSearchResults.SelectedItem as ListBoxItem;
        var revealFocused = item?.Content is Grid row && ReferenceEquals(Keyboard.FocusedElement, row.Children.OfType<Button>().Last());
        if (Keyboard.Modifiers == ModifierKeys.Control || revealFocused) RevealSearchResult(item);
        else RunSearchResult(item);
    }

    void RunSearchResult(ListBoxItem? item)
    {
        // Check the live menu again: selection, mode or a scan may have changed since matching.
        if (item?.Tag is not ButtonBase command || !command.IsEnabled || !SearchableCommands().Contains(command) ||
            !MainActionAvailability.CanExecute(CommandState.GetId(command), SelectedRaw().Count)) return;
        CommandSearchPopup.IsOpen = false;
        CommandSearchBox.Clear();
        ClientGrid.Focus();
        if (command is ToggleButton toggle) toggle.IsChecked = toggle.IsChecked != true;
        command.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, command));
    }
}
