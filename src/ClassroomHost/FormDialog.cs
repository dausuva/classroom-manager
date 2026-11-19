// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using ClassroomManager.Shared;
using Microsoft.Win32;

namespace ClassroomManager.Host;

/// <summary>Small code-built form dialog so we don't need a XAML file per prompt.</summary>
public sealed class FormDialog : Window
{
    readonly StackPanel _panel = new() { Margin = new Thickness(14) };
    readonly Dictionary<string, Func<string>> _values = new();
    readonly Dictionary<string, Control> _controls = new();
    FrameworkElement? _initialFocus;
    readonly List<(string Key, string Message)> _required = new();
    TextBlock? _problem;
    Func<FormDialog, Task>? _loadAsync;
    string _loadingText = "Loading...";

    public FormDialog(string title, Window? owner)
    {
        Title = title;
        Owner = owner;
        Width = 480;
        MinWidth = 360;
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height * 0.85);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer
        {
            Content = _panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Loaded += (_, _) =>
        {
            if (_initialFocus is null) return;
            _initialFocus.Focus();
            Keyboard.Focus(_initialFocus);
        };
    }

    /// <summary>
    /// Keeps the dialog open, with <paramref name="message"/> shown in it, while the field holds nothing or
    /// only spaces. Without this OK would close the dialog and the caller would have to ignore the answer.
    /// </summary>
    public FormDialog Require(string key, string message)
    {
        _required.Add((key, message));
        return this;
    }

    /// <summary>The message of the first required field that is still blank, or null when all are filled in.</summary>
    public string? FirstProblem() => _required.Where(r => string.IsNullOrWhiteSpace(_values[r.Key]())).Select(r => r.Message).FirstOrDefault();

    void ShowProblem(string message)
    {
        if (_problem is null) return;
        _problem.Text = message;
        _problem.Visibility = Visibility.Visible;
        var key = _required.FirstOrDefault(r => r.Message == message).Key;
        if (key is not null && _controls.TryGetValue(key, out var field))
        {
            field.Focus();
            Keyboard.Focus(field);
        }
    }

    public FormDialog Note(string text, bool preserveText = false)
    {
        var block = Ui.MutedText(text, new Thickness(0, 0, 0, 8));
        if (preserveText) UiTranslator.SetPreserveText(block, true);
        _panel.Children.Add(block);
        return this;
    }

    /// <summary>A small picture in plain ASCII, drawn in a fixed-width font so the lines stay aligned. Never translated.</summary>
    public FormDialog Art(IReadOnlyList<string> lines)
    {
        // Monospace only, every glyph the same whole-pixel width and every line the same height, so the picture is not skewed (a proportional
        // fallback or sub-pixel positioning shifts the columns).
        const double size = 13;
        var block = new TextBlock
        {
            Text = string.Join(Environment.NewLine, lines), FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Lucida Console, Courier New"),
            FontSize = size, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = size + 2, Margin = new Thickness(24, 0, 0, 8), TextWrapping = TextWrapping.NoWrap,
        };
        System.Windows.Media.TextOptions.SetTextFormattingMode(block, System.Windows.Media.TextFormattingMode.Display);
        System.Windows.Media.TextOptions.SetTextRenderingMode(block, System.Windows.Media.TextRenderingMode.ClearType);
        block.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        UiTranslator.SetPreserveText(block, true);
        _panel.Children.Add(block);
        return this;
    }

    /// <summary>A sentence in the danger colour, for what a person must read before answering.</summary>
    public FormDialog Warning(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
        block.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        _panel.Children.Add(block);
        return this;
    }

    /// <summary>A value to be compared by eye (a fingerprint): monospaced, selectable and read-only.</summary>
    public FormDialog Fingerprint(string label, string value)
    {
        var box = new TextBox
        {
            Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 15, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 8),
        };
        AddLabel(label, box);
        _panel.Children.Add(box);
        return this;
    }

    public FormDialog Target(IReadOnlyCollection<ClientInfo> targets)
    {
        var target = OperationTargetSummary.Format(targets, UiTranslator.GetPreserveText(this) ? Loc.Current : null);
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock
            {
                Text = target.Heading + "\n" + target.Detail,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
            },
        };
        card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        UiTranslator.SetPreserveText(card, UiTranslator.GetPreserveText(this)); // Opt-in forms use typed core formatting, not pattern-matched names.
        card.SetResourceReference(Border.BackgroundProperty, "HeaderBackgroundBrush");
        _panel.Children.Add(card);
        return this;
    }

    /// <summary>Opens first, then populates controls without blocking the UI.</summary>
    public FormDialog LoadAsync(string loadingText, Func<FormDialog, Task> loadAsync)
    {
        _loadingText = loadingText;
        _loadAsync = loadAsync;
        return this;
    }

    public FormDialog Text(string key, string label, string def = "", bool password = false, bool multiline = false)
    {
        // A suggested text of the program ("Please look at the teacher.") is offered in the teacher's language;
        // anything else in the box (a list read from a computer, a name) is not the program's and stays.
        def = Loc.Current.Exact(def);
        if (password)
        {
            var pb = new PasswordBox { Password = def, Margin = new Thickness(0, 0, 0, 8) };
            AddLabel(label, pb);
            var shown = new TextBox { Text = def, Visibility = Visibility.Collapsed };
            var reveal = new CheckBox
            {
                Content = "👁 _Show",
                ToolTip = "Show or hide this value. While shown, it can be selected and copied.",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 8),
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(reveal, 1);
            row.Children.Add(pb);
            row.Children.Add(shown);
            row.Children.Add(reveal);
            reveal.Checked += (_, _) =>
            {
                shown.Text = pb.Password;
                pb.Visibility = Visibility.Collapsed;
                shown.Visibility = Visibility.Visible;
                shown.Focus();
                shown.SelectAll();
            };
            reveal.Unchecked += (_, _) =>
            {
                pb.Password = shown.Text;
                shown.Visibility = Visibility.Collapsed;
                pb.Visibility = Visibility.Visible;
                pb.Focus();
            };
            _panel.Children.Add(row);
            RememberInitialFocus(pb);
            _values[key] = () => reveal.IsChecked == true ? shown.Text : pb.Password;
        }
        else
        {
            var tb = new TextBox
            {
                Text = def,
                Margin = new Thickness(0, 0, 0, 8),
                AcceptsReturn = multiline,
                MinLines = multiline ? 2 : 1,
                MaxLines = multiline ? 8 : 1,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
            };
            AddLabel(label, tb);
            _panel.Children.Add(tb);
            RememberInitialFocus(tb);
            _values[key] = () => tb.Text;
            _controls[key] = tb;
        }
        return this;
    }

    /// <summary>
    /// Fills a text field with a value that depends on a choice, each time the choice changes, until the
    /// teacher types something of their own there.
    /// </summary>
    public FormDialog Suggest(string choiceKey, string textKey, Func<string, string> suggestion)
    {
        if (!_controls.TryGetValue(choiceKey, out var choice) || choice is not ComboBox combo ||
            !_controls.TryGetValue(textKey, out var field) || field is not TextBox box) return this;
        var suggested = box.Text;
        combo.SelectionChanged += (_, _) =>
        {
            if (box.Text != suggested) return;
            suggested = box.Text = suggestion(combo.SelectedItem as string ?? "");
        };
        return this;
    }

    public FormDialog ExpandableText(string key, string label, string def = "")
    {
        var expand = new CheckBox
        {
            Content = "Use _multi-line command editor",
            Margin = new Thickness(0, 0, 0, 4),
        };
        var tb = new TextBox { Text = def, Margin = new Thickness(0, 0, 0, 8) };
        void UpdateEditor()
        {
            var enabled = expand.IsChecked == true;
            tb.AcceptsReturn = enabled;
            tb.MinLines = enabled ? 2 : 1;
            tb.MaxLines = enabled ? 10 : 1;
            tb.TextWrapping = enabled ? TextWrapping.Wrap : TextWrapping.NoWrap;
            tb.VerticalScrollBarVisibility = enabled ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        }
        expand.Checked += (_, _) => UpdateEditor();
        expand.Unchecked += (_, _) => UpdateEditor();
        AddLabel(label, tb);
        _panel.Children.Add(expand);
        _panel.Children.Add(tb);
        RememberInitialFocus(tb);
        _values[key] = () => tb.Text;
        return this;
    }

    /// <summary>A button inside the form that does something at once, such as opening a related window.</summary>
    public FormDialog Button(string label, string toolTip, Action click)
    {
        var button = new Button { Content = label, ToolTip = toolTip, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        button.Click += (_, _) => click();
        _panel.Children.Add(button);
        return this;
    }

    public FormDialog Check(string key, string label, bool def = false)
    {
        // The label as wrapping text: a long explanation was cut off at the edge of the dialog. AccessText keeps
        // the underscore's meaning (Alt+letter), which a plain text block would show as an underscore.
        var cb = new CheckBox
        {
            Content = new AccessText { Text = label, TextWrapping = TextWrapping.Wrap },
            IsChecked = def, Margin = new Thickness(0, 0, 0, 8),
        };
        AutomationProperties.SetName(cb, label.Replace("_", ""));
        _panel.Children.Add(cb);
        RememberInitialFocus(cb);
        _values[key] = () => cb.IsChecked == true ? "true" : "false";
        _controls[key] = cb;
        return this;
    }

    /// <summary>Hide dependent settings immediately without discarding their saved values.</summary>
    public FormDialog ShowWhenChecked(string toggleKey, params string[] keys)
    {
        var toggle = (CheckBox)_controls[toggleKey];
        void Refresh()
        {
            foreach (var key in keys)
            {
                var field = _controls[key];
                field.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                foreach (var label in _panel.Children.OfType<Label>().Where(label => ReferenceEquals(label.Target, field)))
                    label.Visibility = field.Visibility;
            }
        }
        toggle.Checked += (_, _) => Refresh();
        toggle.Unchecked += (_, _) => Refresh();
        Refresh();
        return this;
    }

    public FormDialog Combo(string key, string label, string[] items, int selected = 0)
    {
        var combo = new ComboBox { ItemsSource = items, SelectedIndex = selected, Margin = new Thickness(0, 0, 0, 8) };
        AddLabel(label, combo);
        _panel.Children.Add(combo);
        RememberInitialFocus(combo);
        _values[key] = () => combo.SelectedItem as string ?? "";
        _controls[key] = combo;
        return this;
    }

    /// <summary>Already translated display labels with stable command values. No pattern matching of inventory data.</summary>
    public FormDialog DisplayCombo(string key, string label, IReadOnlyList<(string Value, string Shown)> items, int selected = 0)
    {
        var combo = new ComboBox { ItemsSource = items.Select(item => new FolderChoiceItem(item.Value, item.Shown)).ToArray(),
            DisplayMemberPath = nameof(FolderChoiceItem.Shown), SelectedIndex = selected, Margin = new Thickness(0, 0, 0, 8) };
        UiTranslator.SetPreserveText(combo, true);
        AddLabel(label, combo);
        _panel.Children.Add(combo);
        RememberInitialFocus(combo);
        _values[key] = () => (combo.SelectedItem as FolderChoiceItem)?.Value ?? "";
        _controls[key] = combo;
        return this;
    }

    /// <summary>
    /// A text field with a drop-down of usual values: picking one puts its value in the field, and anything
    /// else can still be typed. Each choice is the value and the name shown for it in the list.
    /// </summary>
    public FormDialog Choice(string key, string label, IReadOnlyList<(string Value, string Name)> choices, string def = "")
    {
        var combo = EditableCombo(choices, def);
        combo.Margin = new Thickness(0, 0, 0, 8);
        AddLabel(label, combo);
        _panel.Children.Add(combo);
        RememberInitialFocus(combo);
        _values[key] = () => PathText.Clean(combo.Text);
        _controls[key] = combo;
        return this;
    }

    static ComboBox EditableCombo(IReadOnlyList<(string Value, string Name)> choices, string def)
    {
        var combo = new ComboBox
        {
            IsEditable = true,
            ItemsSource = choices.Select(choice => new FolderChoiceItem(choice.Value,
                choice.Name.Length > 0 && choice.Name != choice.Value ? $"{Loc.T(choice.Name)}  ({choice.Value})" : choice.Value)).ToList(),
            DisplayMemberPath = nameof(FolderChoiceItem.Shown),
        };
        // The field shows the value itself (a path or %TOKEN%), not the name the list shows for it.
        TextSearch.SetTextPath(combo, nameof(FolderChoiceItem.Value));
        combo.Text = def;
        return combo;
    }

    sealed record FolderChoiceItem(string Value, string Shown);

    public FormDialog MultiSelect(string key, string label, IReadOnlyList<string> items, IEnumerable<string>? selected = null,
        bool filter = false, IReadOnlyList<(string Label, IReadOnlyList<string> Values)>? presets = null)
    {
        if (filter) return FilteredMultiSelect(key, label, items, selected);

        var list = new ListBox { ItemsSource = items, SelectionMode = SelectionMode.Multiple, Height = Math.Min(260, Math.Max(80, items.Count * 26)), Margin = new Thickness(0, 0, 0, 8) };
        var selectedSet = selected?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddLabel(label, list);
        if (presets is { Count: > 0 })
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var preset in presets)
            {
                var button = new Button { Content = preset.Label, Margin = new Thickness(0, 0, 8, 4) };
                button.Click += (_, _) =>
                {
                    var values = preset.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    list.SelectedItems.Clear();
                    foreach (var item in items.Where(values.Contains)) list.SelectedItems.Add(item);
                };
                row.Children.Add(button);
            }
            _panel.Children.Add(row);
        }
        list.Loaded += (_, _) =>
        {
            for (var i = 0; i < items.Count; i++)
                if (selectedSet.Contains(items[i])) list.SelectedItems.Add(items[i]);
        };
        _panel.Children.Add(list);
        RememberInitialFocus(list);
        _values[key] = () => string.Join("\n", list.SelectedItems.Cast<string>());
        return this;
    }

    /// <summary>A multi-select list with a search box; entries hidden by the search stay selected.</summary>
    FormDialog FilteredMultiSelect(string key, string label, IReadOnlyList<string> items, IEnumerable<string>? selected)
    {
        var selection = new FilteredSelection(items, selected);
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 4), ToolTip = "Type part of a name to narrow the list. Hidden entries keep their selection." };
        var list = new ListBox { SelectionMode = SelectionMode.Multiple, Height = Math.Min(260, Math.Max(80, items.Count * 26)), Margin = new Thickness(0, 0, 0, 2) };
        var summary = Ui.MutedText("", new Thickness(0, 0, 0, 8));
        AutomationProperties.SetLiveSetting(summary, AutomationLiveSetting.Polite);
        var refreshing = false;

        void UpdateSummary() =>
            summary.Text = $"{selection.ChosenCount} selected · showing {list.Items.Count} of {selection.TotalCount}";
        void Refresh()
        {
            refreshing = true;
            try
            {
                list.ItemsSource = selection.Visible(search.Text);
                foreach (var item in list.Items.Cast<string>().Where(selection.IsChosen)) list.SelectedItems.Add(item);
            }
            finally { refreshing = false; }
            UpdateSummary();
        }

        list.SelectionChanged += (_, e) =>
        {
            if (refreshing) return;
            selection.Choose(e.AddedItems.Cast<string>());
            selection.Unchoose(e.RemovedItems.Cast<string>());
            UpdateSummary();
        };
        search.TextChanged += (_, _) => Refresh();
        list.Loaded += (_, _) => Refresh();

        AddLabel(label, list);
        var searchRow = new DockPanel();
        var searchLabel = Ui.Label("_Search:", search, new Thickness(0, 0, 6, 4));
        DockPanel.SetDock(searchLabel, Dock.Left);
        searchRow.Children.Add(searchLabel);
        searchRow.Children.Add(search);
        _panel.Children.Add(searchRow);
        _panel.Children.Add(list);
        _panel.Children.Add(summary);
        RememberInitialFocus(search);
        _values[key] = () => string.Join("\n", selection.Chosen);
        return this;
    }

    public FormDialog File(string key, string label, string filter)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var browse = new Button { Content = "_Browse...", ToolTip = "Choose a file using the Windows file picker", Margin = new Thickness(6, 0, 0, 0) };
        UiTranslator.SetPreserveText(browse, false);
        var tb = new TextBox();
        AddLabel(label, tb);
        DockPanel.SetDock(browse, Dock.Right);
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = filter };
            if (dlg.ShowDialog(this) == true) tb.Text = dlg.FileName;
        };
        row.Children.Add(browse);
        row.Children.Add(tb);
        _panel.Children.Add(row);
        RememberInitialFocus(tb);
        _values[key] = () => tb.Text;
        return this;
    }

    /// <summary>
    /// A folder on this computer: typed, picked with Browse, or chosen from <paramref name="choices"/>
    /// (value and name), starting at <paramref name="def"/>.
    /// </summary>
    public FormDialog Folder(string key, string label, string def = "", IReadOnlyList<(string Value, string Name)>? choices = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var browse = new Button { Content = "_Browse...", ToolTip = "Choose a folder using the Windows folder picker", Margin = new Thickness(6, 0, 0, 0) };
        UiTranslator.SetPreserveText(browse, false);
        var combo = EditableCombo(choices ?? [], def);
        var create = new Button { Content = "Create new folder", Margin = new Thickness(6, 0, 0, 0) };
        create.Click += (_, _) => LocalFolderCreation.Prompt(this, () => combo.Text, path => combo.Text = path);
        AddLabel(label, combo);
        DockPanel.SetDock(browse, Dock.Right);
        DockPanel.SetDock(create, Dock.Right);
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = label };
            if (System.IO.Directory.Exists(combo.Text.Trim())) dlg.InitialDirectory = combo.Text.Trim();
            if (dlg.ShowDialog(this) == true) combo.Text = dlg.FolderName;
        };
        row.Children.Add(browse);
        row.Children.Add(create);
        row.Children.Add(combo);
        _panel.Children.Add(row);
        RememberInitialFocus(combo);
        _values[key] = () => PathText.Clean(combo.Text);
        _controls[key] = combo;
        return this;
    }

    /// <summary>Shows the dialog; returns true if the user pressed OK.</summary>
    public bool Ask(string okText = "OK")
    {
        StackPanel? loading = null;
        TextBlock? loadingStatus = null;
        if (_loadAsync is not null)
        {
            loadingStatus = new TextBlock
            {
                Text = _loadingText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            };
            AutomationProperties.SetLiveSetting(loadingStatus, AutomationLiveSetting.Polite);
            var progress = new ProgressBar { IsIndeterminate = true, Height = 5 };
            AutomationProperties.SetName(progress, _loadingText);
            loading = new StackPanel { Margin = new Thickness(0, 4, 0, 10) };
            loading.Children.Add(loadingStatus);
            loading.Children.Add(progress);
            _panel.Children.Add(loading);
        }
        _problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        _problem.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        AutomationProperties.SetLiveSetting(_problem, AutomationLiveSetting.Assertive);
        _panel.Children.Add(_problem);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        UiTranslator.SetPreserveText(buttons, false); // Review/cancel and their tooltips remain core-owned.
        var ok = new Button
        {
            // Translated before the shortcut letter is added: the catalogue knows "Lock", not "_Lock".
            Content = AccessKey(Loc.T(okText)),
            IsDefault = !IsConsequential(okText),
            IsEnabled = _loadAsync is null,
            MinWidth = 88,
            ToolTip = $"{okText} — apply the values in this dialog",
        };
        var cancel = new Button
        {
            Content = "_Cancel",
            IsCancel = true,
            MinWidth = 88,
            Margin = new Thickness(0),
            ToolTip = "Cancel — close without applying changes",
        };
        ok.Click += (_, _) =>
        {
            if (FirstProblem() is { } problem) { ShowProblem(problem); return; }
            DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        _panel.Children.Add(buttons);
        if (_loadAsync is not null)
        {
            Loaded += async (_, _) =>
            {
                var loader = _loadAsync;
                _loadAsync = null;
                if (loader is null || loading is null || loadingStatus is null) return;
                try
                {
                    await loader(this);
                    _panel.Children.Remove(loading);
                    _panel.Children.Remove(buttons);
                    _panel.Children.Add(buttons);
                    ok.IsEnabled = true;
                    if (_initialFocus is not null)
                    {
                        _initialFocus.Focus();
                        Keyboard.Focus(_initialFocus);
                    }
                }
                catch (Exception ex)
                {
                    loading.Children.OfType<ProgressBar>().Single().Visibility = Visibility.Collapsed;
                    loadingStatus.Text = "Could not load configuration: " + ex.Message;
                    UiTranslator.SetPreserveText(loadingStatus, false);
                    loadingStatus.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                }
            };
        }
        return ShowDialog() == true;
    }

    public string S(string key) => _values[key]().Trim();

    /// <summary>The value exactly as entered, for input such as passwords where surrounding spaces count.</summary>
    public string Raw(string key) => _values[key]();
    public bool B(string key) => _values[key]() == "true";

    void AddLabel(string label, FrameworkElement target)
    {
        var prompt = new Label
        {
            Content = UiTranslator.GetPreserveText(this) ? new AccessText { Text = label, TextWrapping = TextWrapping.Wrap } : label,
            Target = target,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 2),
        };
        AutomationProperties.SetName(target, label.Replace("_", ""));
        _panel.Children.Add(prompt);
    }

    void RememberInitialFocus(FrameworkElement element) => _initialFocus ??= element;

    static string AccessKey(string text) => text.Contains('_') ? text : "_" + text;

    static bool IsConsequential(string text)
    {
        var value = text.Replace("_", "");
        return new[] { "delete", "remove", "install", "uninstall", "reset", "block", "clear", "shut", "restart", "rotate" }
            .Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
