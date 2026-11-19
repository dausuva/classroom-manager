// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Interactive shell on one student computer (cmd or PowerShell, elevated or as the student).</summary>
public sealed class TerminalWindow : Window
{
    static readonly Regex AnsiControl = new("\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\a]*(?:\\a|\\x1B\\\\))", RegexOptions.Compiled);
    ClientInfo _client;
    readonly HostSettings _settings;
    readonly ComboBox _shell = new() { ItemsSource = new[] { "cmd", "PowerShell" }, SelectedIndex = 1, Width = 100 };
    readonly ComboBox _level = new()
    {
        ItemsSource = new[] { "Elevated (SYSTEM)", "Standard (signed-in user)" },
        SelectedIndex = 1,
        Width = 190,
        Margin = new Thickness(6, 0, 0, 0),
    };
    readonly Button _start = new() { Content = "_Start", MinWidth = 70, Margin = new Thickness(6, 0, 0, 0) };
    readonly TextBox _output = new()
    {
        IsReadOnly = true,
        FontFamily = new FontFamily("Consolas"),
        Background = Brushes.Black,
        Foreground = Brushes.LightGray,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 8, 0, 8),
    };
    readonly TextBox _input = new() { FontFamily = new FontFamily("Consolas"), IsEnabled = false };
    readonly CheckBox _multiline = new()
    {
        Content = "_Multi-line input (Ctrl+Enter sends)",
        ToolTip = "Enter several command lines; press Ctrl+Enter to send them together.",
        Margin = new Thickness(10, 0, 0, 0),
    };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    readonly SemaphoreSlim _gate = new(1, 1);

    ClientSession? _session;
    string? _id;
    string _levelValue = Levels.User;
    bool _localEcho;
    readonly List<string> _history = new();
    int _historyPos;

    public TerminalWindow(ClientInfo client, HostSettings settings, Window owner)
    {
        _client = client;
        _settings = settings;
        Owner = owner;
        Title = $"Terminal - {client.Name}";
        Width = 860;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var top = new WrapPanel();
        foreach (var item in ComputerPicker.ForRow(new ComputerPicker(client, SwitchTo, () => _id is not null
            ? Loc.F($"A terminal session is open on {_client.Name}. Close this window to end it, then open the terminal for the other computer.") : null)))
            top.Children.Add(item);
        top.Children.Add(Ui.Label("_Shell:", _shell, new Thickness(12, 0, 6, 0)));
        top.Children.Add(_shell);
        top.Children.Add(Ui.Label("_Privilege:", _level, new Thickness(12, 0, 6, 0)));
        top.Children.Add(_level);
        top.Children.Add(_start);
        top.Children.Add(_multiline);

        System.Windows.Automation.AutomationProperties.SetName(_output, "Terminal output");
        System.Windows.Automation.AutomationProperties.SetName(_input, "Terminal input");

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_input, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_input);
        root.Children.Add(_output);
        Content = root;

        _start.Click += async (_, _) => await StartAsync();
        _timer.Tick += async (_, _) => await PollAsync();
        _input.PreviewKeyDown += Input_KeyDown;
        _multiline.Checked += (_, _) => UpdateInputMode();
        _multiline.Unchecked += (_, _) => UpdateInputMode();
        Closing += (_, _) => Shutdown();
    }

    /// <summary>The computer was changed in the picker (only while no session is open): the next Start opens on it.</summary>
    void SwitchTo(ClientInfo computer)
    {
        _client = computer;
        Title = $"Terminal - {computer.Name}";
        _output.Clear();
        AppendOutput("[" + ComputerPicker.NowShowing(computer) + "]\n");
    }

    async Task StartAsync()
    {
        if (_id is not null) return;
        _start.IsEnabled = _shell.IsEnabled = _level.IsEnabled = false;
        try
        {
            _levelValue = _level.SelectedIndex == 1 ? Levels.User : Levels.System;
            var shell = _shell.SelectedIndex == 1 ? "powershell" : "cmd";
            _localEcho = shell == "powershell";

            _session = await ClientSession.OpenAsync(_client, _settings);
            var data = await _session.CallAtAsync(_levelValue, Cmd.TermStart, new JsonObject { ["shell"] = shell });
            _id = data["id"]!.GetValue<string>();
            _input.IsEnabled = true;
            _input.Focus();
            _timer.Start();
        }
        catch (Exception ex)
        {
            AppendOutput($"[could not start: {ex.Message}]\n");
            _start.IsEnabled = _shell.IsEnabled = _level.IsEnabled = true;
        }
    }

    async void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up && _history.Count > 0)
        {
            _historyPos = Math.Max(0, _historyPos - 1);
            _input.Text = _history[_historyPos];
            _input.CaretIndex = _input.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _history.Count > 0)
        {
            _historyPos = Math.Min(_history.Count, _historyPos + 1);
            _input.Text = _historyPos < _history.Count ? _history[_historyPos] : "";
            _input.CaretIndex = _input.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (_multiline.IsChecked != true || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
        {
            e.Handled = true;
            var text = _input.Text;
            _input.Clear();
            if (text.Length > 0)
            {
                _history.Add(text);
                if (_history.Count > UiBufferLimits.TerminalHistoryEntries) _history.RemoveAt(0);
                _historyPos = _history.Count;
            }
            if (_localEcho) AppendOutput(text + "\n");
            await SendAsync(text + "\r\n");
        }
    }

    void UpdateInputMode()
    {
        var enabled = _multiline.IsChecked == true;
        _input.AcceptsReturn = enabled;
        _input.MinLines = enabled ? 2 : 1;
        _input.MaxLines = enabled ? 6 : 1;
        _input.VerticalScrollBarVisibility = enabled ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
    }

    async Task SendAsync(string text)
    {
        if (_session is null || _id is null) return;
        await _gate.WaitAsync();
        try { await _session.CallAtAsync(_levelValue, Cmd.TermWrite, new JsonObject { ["id"] = _id, ["text"] = text }); }
        catch (Exception ex) { AppendOutput($"[send failed: {ex.Message}]\n"); }
        finally { _gate.Release(); }
    }

    async Task PollAsync()
    {
        if (_session is null || _id is null) return;
        if (!await _gate.WaitAsync(0)) return; // a send is in progress; try again next tick
        try
        {
            var data = await _session.CallAtAsync(_levelValue, Cmd.TermRead, new JsonObject { ["id"] = _id });
            var text = data["output"]?.GetValue<string>() ?? "";
            if (text.Length > 0)
            {
                AppendOutput(AnsiControl.Replace(text, "").Replace("\r\n", "\n"));
                _output.ScrollToEnd();
            }
            if (data["exited"]?.GetValue<bool>() == true)
            {
                AppendOutput("\n[shell exited]\n");
                _timer.Stop();
                _input.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            _timer.Stop();
            _input.IsEnabled = false;
            AppendOutput($"\n[connection lost: {ex.Message}]\n");
        }
        finally { _gate.Release(); }
    }

    void AppendOutput(string text) => UiBufferLimits.Append(_output, text, UiBufferLimits.TerminalCharacters);

    void Shutdown()
    {
        _timer.Stop();
        var session = _session;
        var id = _id;
        _session = null;
        _id = null;
        if (session is null) return;

        // best effort: end the remote shell, then drop the connection
        _ = Task.Run(async () =>
        {
            try
            {
                if (id is not null)
                    await session.CallAtAsync(_levelValue, Cmd.TermClose, new JsonObject { ["id"] = id });
            }
            catch { }
            finally { session.Dispose(); }
        });
    }
}
