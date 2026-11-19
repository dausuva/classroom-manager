// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ClassroomManager.Host;

/// <summary>Makes, changes, orders and removes the teacher's own quick actions.</summary>
public sealed class CustomQuickActionsWindow : Window
{
    readonly HostSettings _settings;
    readonly Action _changed;
    readonly ListView _list = new() { SelectionMode = SelectionMode.Single, MinHeight = 160 };

    public CustomQuickActionsWindow(HostSettings settings, Window? owner, Action? changed = null)
    {
        _settings = settings;
        _changed = changed ?? (() => { });
        Owner = owner;
        Title = "Custom quick actions";
        Width = 760;
        Height = 460;
        MinWidth = 560;
        MinHeight = 320;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Name", Width = 200, DisplayMemberBinding = new Binding(nameof(CustomQuickAction.Name)) });
        grid.Columns.Add(new GridViewColumn { Header = "Does", Width = 500, DisplayMemberBinding = new Binding(nameof(CustomQuickAction.Summary)) });
        _list.View = grid;
        _list.MouseDoubleClick += (_, _) => Edit();

        var add = new Button { Content = "_New...", Tag = FluentIcons.Add, ToolTip = "New — make a quick action that runs one action with settings you choose." };
        add.Click += (_, _) => New();
        var edit = new Button { Content = "_Edit...", Tag = FluentIcons.Edit, ToolTip = "Edit — change the selected quick action." };
        edit.Click += (_, _) => Edit();
        var remove = new Button { Content = "_Remove", Tag = FluentIcons.Delete, ToolTip = "Remove — delete the selected quick action." };
        remove.Click += (_, _) => Remove();
        var up = new Button { Content = "Move _up", Tag = FluentIcons.Up, ToolTip = "Move up — show the selected button earlier in Actions." };
        up.Click += (_, _) => Move(-1);
        var down = new Button { Content = "Move _down", Tag = FluentIcons.Down, ToolTip = "Move down — show the selected button later in Actions." };
        down.Click += (_, _) => Move(1);
        var close = new Button { Content = "_Close", Tag = FluentIcons.Close, IsCancel = true };
        close.Click += (_, _) => Close();

        var buttons = Ui.Spaced(new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { add, edit, remove, up, down, close } });
        var note = Ui.Explanation(
            "Each custom button appears in Actions and runs on the computers selected at that moment. " +
            "Actions that can lose work ask first. Programs open as the signed-in user.",
            new Thickness(0, 0, 0, 8));

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;
        Refresh();
    }

    void Refresh(CustomQuickAction? select = null)
    {
        _list.ItemsSource = null;
        _list.ItemsSource = _settings.CustomQuickActions;
        if (select is not null) _list.SelectedItem = select;
    }

    void Saved(CustomQuickAction? select = null)
    {
        _settings.Save();
        Refresh(select);
        _changed();
    }

    void New()
    {
        var kinds = new FormDialog("New quick action", this)
            .Combo("kind", "What should it do?", CustomQuickActionKinds.All.Select(entry => entry.Label).ToArray());
        if (!kinds.Ask("Next")) return;
        var kind = CustomQuickActionKinds.All.First(entry => entry.Label == kinds.S("kind")).Kind;
        var action = new CustomQuickAction { Kind = kind };
        if (!AskSettings(action)) return;
        _settings.CustomQuickActions.Add(action);
        Saved(action);
    }

    void Edit()
    {
        if (_list.SelectedItem is not CustomQuickAction selected) return;
        var copy = selected.Copy();
        if (!AskSettings(copy)) return;
        var index = _settings.CustomQuickActions.IndexOf(selected);
        _settings.CustomQuickActions[index] = copy;
        Saved(copy);
    }

    void Remove()
    {
        if (_list.SelectedItem is not CustomQuickAction selected) return;
        if (MessageBox.Show(this, $"Remove the quick action \"{selected.Name}\"?", "Custom quick actions",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _settings.CustomQuickActions.Remove(selected);
        Saved();
    }

    void Move(int step)
    {
        if (_list.SelectedItem is not CustomQuickAction selected) return;
        var list = _settings.CustomQuickActions;
        var index = list.IndexOf(selected);
        var target = index + step;
        if (target < 0 || target >= list.Count) return;
        list.RemoveAt(index);
        list.Insert(target, selected);
        Saved(selected);
    }

    /// <summary>The settings form of the action's kind; asks again until they are usable or the teacher cancels.</summary>
    bool AskSettings(CustomQuickAction action)
    {
        while (true)
        {
            var dlg = new FormDialog(CustomQuickActionKinds.Label(action.Kind), this)
                .Text("name", "Name in the Quick actions bar", action.Name);
            switch (action.Kind)
            {
                case CustomQuickActionKinds.OpenProgram:
                    dlg.Text("target", "Program path or executable name (environment variables such as %WINDIR% work)", action.Target)
                        .Text("args", "Arguments (optional)", action.Arguments);
                    break;
                case CustomQuickActionKinds.OpenWebsite:
                    dlg.Text("target", "Web address", action.Target.Length > 0 ? action.Target : "https://");
                    break;
                case CustomQuickActionKinds.Message:
                case CustomQuickActionKinds.Lock:
                    dlg.Text("text", "Message shown to students", action.Text, multiline: true);
                    break;
                case CustomQuickActionKinds.Power:
                    dlg.Combo("power", "Action", CustomQuickActionKinds.PowerActions, Math.Max(0, Array.IndexOf(CustomQuickActionKinds.PowerActions, action.PowerAction)))
                        .Text("delay", "Delay in seconds (0 to 3600)", action.DelaySeconds.ToString())
                        .Text("text", "Message shown to students before shutdown or restart (optional)", action.Text)
                        .Note("Sleep happens at once on the computers, so its delay is waited for here: keep Classroom Manager open until then.");
                    break;
                case CustomQuickActionKinds.SyncFiles:
                    dlg.Choice("target", "Folder on the student computers (choose one, or type a full path)",
                            CollectFolders.OnStudentComputer, action.Target.Length > 0 ? action.Target : "%DOCUMENTS%")
                        .Text("args", "File name pattern (e.g. *.docx, *)", action.Arguments.Length > 0 ? action.Arguments : "*.*")
                        .Check("recursive", "Include subfolders", action.IncludeSubfolders)
                        .Check("overwrite", "Overwrite files already in the destination when the student's copy changed", action.Overwrite)
                        .Folder("local", "Sync into (on this computer; one subfolder per computer)", action.LocalFolder,
                            CollectFolders.OnThisComputer(_settings.RecentCollectFolders))
                        .Note("Like rsync: only files that are new or changed since the last sync are copied. Nothing is deleted on either side.");
                    break;
            }
            if (!dlg.Ask("Save")) return false;

            action.Name = dlg.S("name");
            switch (action.Kind)
            {
                case CustomQuickActionKinds.OpenProgram:
                    action.Target = dlg.S("target");
                    action.Arguments = dlg.S("args");
                    break;
                case CustomQuickActionKinds.OpenWebsite:
                    action.Target = dlg.S("target");
                    break;
                case CustomQuickActionKinds.Message:
                case CustomQuickActionKinds.Lock:
                    action.Text = dlg.S("text");
                    break;
                case CustomQuickActionKinds.Power:
                    action.PowerAction = dlg.S("power");
                    action.DelaySeconds = int.TryParse(dlg.S("delay"), out var delay) ? delay : -1;
                    action.Text = dlg.S("text");
                    break;
                case CustomQuickActionKinds.SyncFiles:
                    action.Target = dlg.S("target");
                    action.Arguments = dlg.S("args");
                    action.IncludeSubfolders = dlg.B("recursive");
                    action.Overwrite = dlg.B("overwrite");
                    action.LocalFolder = dlg.S("local");
                    break;
            }
            var problem = action.Problem();
            if (problem.Length == 0)
            {
                if (action.Kind == CustomQuickActionKinds.SyncFiles) _settings.RememberCollectFolder(action.LocalFolder);
                return true;
            }
            MessageBox.Show(this, problem, "Custom quick actions", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
