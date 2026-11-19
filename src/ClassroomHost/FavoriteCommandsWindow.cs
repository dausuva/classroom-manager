// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class FavoriteCommandsWindow : Window
{
    readonly ListView _list = new() { SelectionMode = SelectionMode.Single };
    readonly List<FavoriteCommand> _commands;

    public List<FavoriteCommand> Commands { get; private set; } = new();

    public FavoriteCommandsWindow(Window owner, IEnumerable<FavoriteCommand> commands)
    {
        Owner = owner;
        Title = "Command favourites";
        Width = 850;
        Height = 500;
        MinWidth = 620;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _commands = FavoriteCommandCatalog.Normalize(commands);

        var grid = new GridView();
        grid.Columns.Add(Col("Name", "Name", 180));
        grid.Columns.Add(Col("Shell", "ShellText", 100));
        grid.Columns.Add(Col("Privilege", "LevelText", 125));
        grid.Columns.Add(Col("Command", "Command", 350));
        _list.View = grid;
        RefreshList();

        var add = MakeButton("_Add...", (_, _) => Edit(null));
        var edit = MakeButton("_Edit...", (_, _) => Edit(_list.SelectedItem as FavoriteCommand));
        var remove = MakeButton("_Remove", (_, _) => Remove());
        var up = MakeButton("Move _up", (_, _) => Move(-1));
        var down = MakeButton("Move _down", (_, _) => Move(1));
        _list.MouseDoubleClick += (_, _) => Edit(_list.SelectedItem as FavoriteCommand);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in new[] { add, edit, remove, up, down }) tools.Children.Add(button);

        var save = new Button { Content = "_Save", IsDefault = true, MinWidth = 88 };
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) =>
        {
            Commands = FavoriteCommandCatalog.Normalize(_commands);
            DialogResult = true;
        };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(save);
        bottom.Children.Add(cancel);

        var note = Ui.Explanation("Saved commands run exactly as written. Do not put passwords, tokens, or other secrets here; favourites are stored in host.json.");
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(tools, Dock.Bottom);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(bottom);
        root.Children.Add(tools);
        root.Children.Add(_list);
        Content = root;
    }

    static GridViewColumn Col(string header, string path, double width) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new Binding(path) };

    static Button MakeButton(string content, RoutedEventHandler click)
    {
        var button = new Button { Content = content, MinWidth = 82, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += click;
        return button;
    }

    void Edit(FavoriteCommand? existing)
    {
        var shell = existing?.Shell == FavoriteCommandShells.PowerShell ? 1 : 0;
        var level = existing?.Level == Levels.User ? 1 : 0;
        var dialog = new FormDialog(existing is null ? "Add command favourite" : "Edit command favourite", this)
            .Text("name", "Name", existing?.Name ?? "")
            .Combo("shell", "Shell", ["Command Prompt (cmd)", "PowerShell"], shell)
            .Combo("level", "Privilege", ["Elevated (SYSTEM)", "Standard (signed-in user)"], level)
            .ExpandableText("command", "Command", existing?.Command ?? "")
            .Note("The selected shell and privilege are applied automatically when this favourite is chosen.");
        if (!dialog.Ask(existing is null ? "Add" : "Save")) return;
        if (dialog.S("name").Length == 0 || dialog.S("command").Length == 0)
        {
            MessageBox.Show(this, "Enter both a name and a command.", "Command favourite", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var value = existing ?? new FavoriteCommand();
        value.Name = dialog.S("name");
        value.Shell = dialog.S("shell") == "PowerShell" ? FavoriteCommandShells.PowerShell : FavoriteCommandShells.CommandPrompt;
        value.Level = dialog.S("level").StartsWith("Standard", StringComparison.Ordinal) ? Levels.User : Levels.System;
        value.Command = dialog.S("command");
        if (existing is null) _commands.Add(value);
        RefreshList(value);
    }

    void Remove()
    {
        if (_list.SelectedItem is not FavoriteCommand selected) return;
        _commands.Remove(selected);
        RefreshList();
    }

    void Move(int offset)
    {
        if (_list.SelectedItem is not FavoriteCommand selected) return;
        var from = _commands.IndexOf(selected);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= _commands.Count) return;
        _commands.RemoveAt(from);
        _commands.Insert(to, selected);
        RefreshList(selected);
    }

    void RefreshList(FavoriteCommand? selected = null)
    {
        _list.ItemsSource = null;
        _list.ItemsSource = _commands;
        if (selected is null) return;
        _list.SelectedItem = selected;
        _list.ScrollIntoView(selected);
    }
}
