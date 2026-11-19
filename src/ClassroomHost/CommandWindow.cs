// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClassroomManager.Shared;
using Microsoft.Win32;

namespace ClassroomManager.Host;

/// <summary>Runs one command line on several computers and shows each computer's output.</summary>
public sealed class CommandWindow : Window
{
    readonly List<ClientInfo> _targets;
    readonly HostSettings _settings;
    readonly ComboBox _favorite = new() { Width = 190, DisplayMemberPath = "Name" };
    readonly ComboBox _shell = new() { ItemsSource = new[] { "cmd", "PowerShell" }, SelectedIndex = 1, Width = 100 };
    readonly ComboBox _level = new()
    {
        ItemsSource = new[] { "Elevated (SYSTEM)", "Standard (signed-in user)" },
        SelectedIndex = 1,
        Width = 190,
        Margin = new Thickness(6, 0, 0, 0),
    };
    readonly TextBox _timeout = new() { Text = "60", Width = 50, Margin = new Thickness(6, 0, 0, 0) };
    readonly TextBox _command = new() { AcceptsReturn = false, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
    readonly CheckBox _multiline = new() { Content = "_Multi-line", ToolTip = "Use a resizable multi-line command editor", Margin = new Thickness(8, 0, 8, 0) };
    readonly Button _saveFavorite = CompactButton("_Save", "★", "Save this command as a favourite for later use");
    readonly Button _loadScript = CompactButton("_Load...", "📄", "Load a PowerShell, cmd, batch, or text file into the command editor");
    readonly RowDefinition _commandRow = new() { Height = GridLength.Auto, MinHeight = 26 };
    readonly RowDefinition _outputRow = new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 80 };
    readonly GridSplitter _editorSplitter = new()
    {
        Height = 6,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center,
        ResizeDirection = GridResizeDirection.Rows,
        Cursor = Cursors.SizeNS,
        Visibility = Visibility.Collapsed,
    };
    readonly TextBox _output = new()
    {
        IsReadOnly = true,
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 8, 0, 0),
    };
    readonly Button _run = CompactButton("_Run", "▶", "Run this command on the selected computers", isDefault: true);

    public CommandWindow(List<ClientInfo> targets, HostSettings settings, Window owner)
    {
        System.Windows.Automation.AutomationProperties.SetName(_output, "Command output");
        _targets = targets;
        _settings = settings;
        Owner = owner;
        Title = $"Run command - {targets.Count} computer(s)";
        Width = 860;
        Height = 560;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        options.Children.Add(Ui.Label("_Favourite:", _favorite));
        options.Children.Add(_favorite);
        options.Children.Add(_saveFavorite);
        options.Children.Add(Ui.Label("_Shell:", _shell));
        options.Children.Add(_shell);
        options.Children.Add(Ui.Label("_Privilege:", _level, new Thickness(12, 0, 6, 0)));
        options.Children.Add(_level);
        options.Children.Add(Ui.Label("_Timeout (seconds):", _timeout, new Thickness(12, 0, 6, 0)));
        options.Children.Add(_timeout);

        var commandRow = new DockPanel();
        DockPanel.SetDock(_run, Dock.Right);
        commandRow.Children.Add(_run);
        DockPanel.SetDock(_multiline, Dock.Right);
        commandRow.Children.Add(_multiline);
        DockPanel.SetDock(_loadScript, Dock.Right);
        commandRow.Children.Add(_loadScript);
        var commandLabel = Ui.Label("_Command:", _command);
        DockPanel.SetDock(commandLabel, Dock.Left);
        commandRow.Children.Add(commandLabel);

        var note = Ui.Explanation(
            "One-shot command, not an interactive terminal (nothing can be typed into it while it runs).",
            new Thickness(0, 4, 0, 6));
        var commandHeader = new StackPanel();
        commandHeader.Children.Add(commandRow);
        commandHeader.Children.Add(note);

        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(_commandRow);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(_outputRow);
        Grid.SetRow(options, 0);
        Grid.SetRow(commandHeader, 1);
        Grid.SetRow(_command, 2);
        Grid.SetRow(_editorSplitter, 3);
        Grid.SetRow(_output, 4);
        root.Children.Add(options);
        root.Children.Add(commandHeader);
        root.Children.Add(_command);
        root.Children.Add(_editorSplitter);
        root.Children.Add(_output);
        Content = root;

        _run.Click += async (_, _) => await RunAsync();
        _multiline.Checked += (_, _) => UpdateCommandEditor();
        _multiline.Unchecked += (_, _) => UpdateCommandEditor();
        _loadScript.Click += (_, _) => LoadScript();
        _saveFavorite.Click += (_, _) => SaveFavorite();
        _favorite.ItemsSource = _settings.FavoriteCommands;
        _favorite.SelectionChanged += (_, _) => ApplyFavorite();
        Loaded += (_, _) => _command.Focus();
    }

    void UpdateCommandEditor()
    {
        var enabled = _multiline.IsChecked == true;
        _command.AcceptsReturn = enabled;
        _command.MinLines = enabled ? 2 : 1;
        _command.MaxLines = enabled ? int.MaxValue : 1;
        _command.VerticalScrollBarVisibility = enabled ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        _editorSplitter.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        _commandRow.MinHeight = enabled ? 80 : 26;
        _commandRow.Height = enabled ? new GridLength(2, GridUnitType.Star) : GridLength.Auto;
        _outputRow.Height = enabled ? new GridLength(3, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
    }

    void SaveFavorite()
    {
        var command = _command.Text.Trim();
        if (command.Length == 0)
        {
            MessageBox.Show(this, "Enter a command before saving it for later.", "Save command",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedName = (_favorite.SelectedItem as FavoriteCommand)?.Name ?? "";
        var dialog = new FormDialog("Save command for later", this)
            .Text("name", "Favourite name", selectedName)
            .Note("Saving with an existing name updates that favourite. The shell, privilege, and complete command text are stored.");
        if (!dialog.Ask("Save")) return;
        var name = dialog.S("name").Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Enter a name for this favourite.", "Save command",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var saved = new FavoriteCommand
        {
            Name = name,
            Command = command,
            Shell = _shell.SelectedIndex == 1 ? FavoriteCommandShells.PowerShell : FavoriteCommandShells.CommandPrompt,
            Level = _level.SelectedIndex == 1 ? Levels.User : Levels.System,
        };
        _settings.FavoriteCommands = FavoriteCommandCatalog.UpsertByName(_settings.FavoriteCommands, saved);
        _settings.Save();
        _favorite.ItemsSource = null;
        _favorite.ItemsSource = _settings.FavoriteCommands;
        _favorite.SelectedItem = _settings.FavoriteCommands.Single(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    void LoadScript()
    {
        var picker = new OpenFileDialog
        {
            Title = "Load command script",
            Filter = "Command scripts (*.ps1;*.cmd;*.bat;*.txt)|*.ps1;*.cmd;*.bat;*.txt|PowerShell scripts (*.ps1)|*.ps1|Command scripts (*.cmd;*.bat)|*.cmd;*.bat|Text files (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var loaded = CommandScriptFile.Load(picker.FileName);
            _command.Text = loaded.Text;
            _shell.SelectedIndex = loaded.Shell == "powershell" ? 1 : 0;
            _multiline.IsChecked = true;
            _command.Focus();
            _command.CaretIndex = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not load script", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void ApplyFavorite()
    {
        if (_favorite.SelectedItem is not FavoriteCommand favorite) return;
        _shell.SelectedIndex = favorite.Shell == FavoriteCommandShells.PowerShell ? 1 : 0;
        _level.SelectedIndex = favorite.Level == Levels.User ? 1 : 0;
        _command.Text = favorite.Command;
        _command.Focus();
        _command.CaretIndex = _command.Text.Length;
    }

    async Task RunAsync()
    {
        var command = _command.Text.Trim();
        if (command.Length == 0) return;
        int.TryParse(_timeout.Text, out var timeout);
        if (timeout <= 0) timeout = 60;

        var shell = _shell.SelectedIndex == 1 ? "powershell" : "cmd";
        var level = _level.SelectedIndex == 1 ? Levels.User : Levels.System;

        _run.IsEnabled = false;
        _output.Clear();
        AppendOutput($"> [{shell}, {(level == Levels.User ? "standard" : "elevated")}] {command}\n\n");

        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(_targets.Select(async c =>
        {
            await gate.WaitAsync();
            string text;
            try
            {
                using var s = await ClientSession.OpenAsync(c, _settings);
                var data = await s.CallAtAsync(level, Cmd.CmdRun, new JsonObject
                {
                    ["shell"] = shell,
                    ["command"] = command,
                    ["timeoutSec"] = timeout,
                });
                bool timedOut = data["timedOut"]?.GetValue<bool>() == true;
                var head = timedOut ? "TIMED OUT (partial output)" : $"exit code {data["exitCode"]?.GetValue<int>()}";
                text = $"=== {c.Name} ({head}) ===\n{data["output"]?.GetValue<string>()}\n";
            }
            catch (Exception ex)
            {
                text = $"=== {c.Name} - FAILED: {ex.Message} ===\n";
            }
            finally { gate.Release(); }

            AppendOutput(text + "\n");
            _output.ScrollToEnd();
        }));

        AppendOutput("--- done ---\n");
        _run.IsEnabled = true;
    }

    void AppendOutput(string text) => UiBufferLimits.Append(_output, text, UiBufferLimits.CommandCharacters);

    static Button CompactButton(string content, string icon, string tooltip, bool isDefault = false) => new()
    {
        Content = content,
        Tag = icon,
        ToolTip = tooltip,
        IsDefault = isDefault,
        MinWidth = 58,
        MinHeight = 28,
        Padding = new Thickness(8, 4, 8, 4),
        Margin = new Thickness(6, 0, 0, 0),
    };
}

public sealed record CommandScriptFile(string Text, string Shell)
{
    public static CommandScriptFile Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The selected script file no longer exists.", path);
        if (info.Length > UiBufferLimits.CommandCharacters * 2L)
            throw new InvalidDataException($"The script is too large. The command editor accepts at most {UiBufferLimits.CommandCharacters:N0} characters.");
        var text = File.ReadAllText(path);
        if (text.Length > UiBufferLimits.CommandCharacters)
            throw new InvalidDataException($"The script is too large. The command editor accepts at most {UiBufferLimits.CommandCharacters:N0} characters.");
        var extension = Path.GetExtension(path);
        var shell = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ? "cmd" : "powershell";
        return new CommandScriptFile(text, shell);
    }
}
