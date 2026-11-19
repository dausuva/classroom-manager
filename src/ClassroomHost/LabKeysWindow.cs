// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Manages the named authentication keys this Host can use across independent labs.</summary>
public sealed class LabKeysWindow : Window
{
    readonly HostSettings _settings;
    readonly ObservableCollection<KeyRow> _rows = new();
    readonly ListView _list = new() { MinHeight = 220 };

    public LabKeysWindow(Window owner, HostSettings settings)
    {
        _settings = settings;
        Owner = owner;
        Title = "Lab keys";
        Width = 720;
        Height = 430;
        MinWidth = 560;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var gridView = new GridView();
        gridView.Columns.Add(new GridViewColumn { Header = "Lab", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KeyRow.Name)), Width = 210 });
        gridView.Columns.Add(new GridViewColumn { Header = "Role", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KeyRow.Role)), Width = 100 });
        gridView.Columns.Add(new GridViewColumn { Header = "Fingerprint", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(KeyRow.Fingerprint)), Width = 180 });
        _list.View = gridView;
        _list.ItemsSource = _rows;

        var add = Button("_Add...", "Add — store the shared key for another lab", Add_Click);
        var edit = Button("_Edit...", "Edit — rename the selected lab or correct its stored key", Edit_Click);
        var remove = Button("_Remove", "Remove — stop trying the selected additional lab key", Remove_Click);
        var makeDefault = Button("Make _default", "Make default — try this lab key first for computers whose key is not known yet", MakeDefault_Click);
        var copy = Button("_Copy key", "Copy key — copy the selected secret key to the clipboard", Copy_Click);
        var close = Button("_Close", "Close — return to the Host", (_, _) => Close());
        close.IsCancel = true;

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in new[] { add, edit, remove, makeDefault, copy, close }) buttons.Children.Add(button);

        var note = Ui.Explanation("Store one named shared key per independently configured lab. The Host remembers which key each computer accepted and tries that key first. Only fingerprints are shown in this list.",
            new Thickness(0, 0, 0, 10));
        var panel = new DockPanel { Margin = new Thickness(14), LastChildFill = true };
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(note);
        panel.Children.Add(buttons);
        panel.Children.Add(_list);
        Content = panel;

        RefreshRows();
        Loaded += (_, _) => _list.Focus();
    }

    static Button Button(string content, string tooltip, RoutedEventHandler click)
    {
        var button = new Button { Content = content, ToolTip = tooltip, MinWidth = 92, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += click;
        return button;
    }

    KeyRow? Selected() => _list.SelectedItem as KeyRow;

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new FormDialog("Add lab key", this)
            .Note("Enter the same shared key installed on the client computers in this lab.")
            .Text("name", "Lab name")
            .Text("key", "Shared key", password: true);
        if (!dlg.Ask("Add")) return;
        if (!Validate(dlg.S("name"), dlg.S("key"), null)) return;

        _settings.AdditionalHostKeys.Add(new NamedHostKey { Name = dlg.S("name"), Psk = dlg.S("key") });
        _settings.Save();
        KeyRefusalPause.ClearAll();
        RefreshRows();
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected is null) { MessageBox.Show(this, "Select a lab key first."); return; }
        var dlg = new FormDialog("Edit lab key", this)
            .Note(selected.IsDefault
                ? "Changing this secret does not update client computers. Use Rotate shared key when you intend to change the installed key."
                : "Changing this secret only corrects what this Host stores; it does not update client computers.")
            .Text("name", "Lab name", selected.Name)
            .Text("key", "Shared key", selected.Psk, password: true);
        if (!dlg.Ask("Save")) return;
        if (!Validate(dlg.S("name"), dlg.S("key"), selected.Id)) return;

        if (selected.IsDefault)
        {
            _settings.DefaultKeyName = dlg.S("name");
            _settings.Psk = dlg.S("key");
        }
        else
        {
            var key = _settings.AdditionalHostKeys.First(x => x.Id == selected.Id);
            key.Name = dlg.S("name");
            key.Psk = dlg.S("key");
        }
        _settings.Save();
        KeyRefusalPause.ClearAll();
        RefreshRows();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected is null) { MessageBox.Show(this, "Select a lab key first."); return; }
        if (selected.IsDefault)
        {
            MessageBox.Show(this, "The default key cannot be removed here. Make another lab the default first.");
            return;
        }
        if (MessageBox.Show(this,
                $"Remove the stored key for '{selected.Name}'?\n\nComputers that use it remain saved, but this Host cannot authenticate to them until the key is added again.",
                "Remove lab key", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _settings.AdditionalHostKeys.RemoveAll(x => x.Id == selected.Id);
        _settings.Save();
        KeyRefusalPause.ClearAll();
        RefreshRows();
    }

    void MakeDefault_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected is null) { MessageBox.Show(this, "Select a lab key first."); return; }
        if (selected.IsDefault) return;
        if (_settings.PreviousPsks.Count > 0)
        {
            MessageBox.Show(this, "Finish the current default-key rotation before changing which lab key is the default.");
            return;
        }

        var replacement = _settings.AdditionalHostKeys.First(x => x.Id == selected.Id);
        _settings.AdditionalHostKeys.Remove(replacement);
        if (!string.IsNullOrWhiteSpace(_settings.Psk))
            _settings.AdditionalHostKeys.Add(new NamedHostKey
            {
                Name = _settings.NormalizedDefaultKeyName(),
                Psk = _settings.Psk,
            });
        _settings.DefaultKeyName = replacement.Name;
        _settings.Psk = replacement.Psk;
        _settings.Save();
        KeyRefusalPause.ClearAll();
        RefreshRows();
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected is null) { MessageBox.Show(this, "Select a lab key first."); return; }
        try { Clipboard.SetText(selected.Psk); }
        catch (Exception ex) { MessageBox.Show(this, "Could not copy the key: " + ex.Message); }
    }

    bool Validate(string name, string key, string? editingId)
    {
        if (string.IsNullOrWhiteSpace(name)) { MessageBox.Show(this, "Enter a lab name."); return false; }
        if (!SharedKey.IsAcceptable(key, out var reason)) { MessageBox.Show(this, reason); return false; }
        var duplicate = RowsFromSettings().Any(x => x.Id != editingId &&
            (x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || x.Psk == key));
        if (duplicate)
        {
            MessageBox.Show(this, "Each lab name and shared key must be unique.");
            return false;
        }
        return true;
    }

    IEnumerable<KeyRow> RowsFromSettings()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Psk))
            yield return new KeyRow("default", _settings.NormalizedDefaultKeyName(), _settings.Psk, true);
        foreach (var key in _settings.AdditionalHostKeys)
            yield return new KeyRow(key.Id, key.Name, key.Psk, false);
    }

    void RefreshRows()
    {
        _rows.Clear();
        foreach (var row in RowsFromSettings()) _rows.Add(row);
        if (_rows.Count > 0) _list.SelectedIndex = 0;
    }

    sealed record KeyRow(string Id, string Name, string Psk, bool IsDefault)
    {
        public string Role => IsDefault ? "Default" : "Additional";
        public string Fingerprint => SharedKey.Fingerprint(Psk);
    }
}
