// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ClassroomManager.Shared;
using Microsoft.Win32;

namespace ClassroomManager.Host;

public enum SoftwareAction
{
    Exe,
    Msi,
    WingetInstall,
    WingetUninstall,
    Registry,
}

/// <summary>Type-aware software dialog that only exposes options used by the selected operation.</summary>
public sealed class InstallSoftwareDialog : Window
{
    static readonly string[] Actions =
    [
        "Install EXE",
        "Install MSI package",
        "Install winget package",
        "Uninstall winget package",
        "Apply registry file",
    ];

    readonly ComboBox _targets;
    readonly TextBox _batch = new() { Text = "8" };
    readonly ComboBox _action = new() { ItemsSource = Actions, SelectedIndex = 0 };
    readonly ComboBox _level = new()
    {
        ItemsSource = new[] { "Elevated (machine-wide, SYSTEM)", "Standard user (signed-in student, per-user apps)" },
        SelectedIndex = 0,
    };
    readonly StackPanel _specific = new();
    readonly TextBox _file = new();
    readonly TextBox _remote = new();
    readonly TextBox _arguments = new();
    readonly TextBox _packageId = new();
    readonly ComboBox _registryView = new()
    {
        ItemsSource = new[] { "Automatic (match the process)", "64-bit registry view", "32-bit registry view" },
        SelectedIndex = 0,
    };
    string _fileFilter = "Executable installers (*.exe)|*.exe";

    public InstallSoftwareDialog(Window owner, string[] targetChoices, int selectedTarget)
    {
        Owner = owner;
        Title = "Install / uninstall software";
        Width = 560;
        MinWidth = 440;
        MaxHeight = Math.Max(520, SystemParameters.WorkArea.Height * 0.85);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _targets = new ComboBox { ItemsSource = targetChoices, SelectedIndex = selectedTarget };
        var form = new StackPanel { Margin = new Thickness(16) };
        form.Children.Add(Field("Install _on", _targets));
        form.Children.Add(Field("How many computers at once (blank = all, 1 = one by one)", _batch));
        form.Children.Add(Field("_Action", _action));
        form.Children.Add(Field("_Run as", _level));
        form.Children.Add(_specific);

        var run = new Button { Content = "_Run", MinWidth = 88 };
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0) };
        run.Click += (_, _) => { if (Validate()) DialogResult = true; };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(run);
        buttons.Children.Add(cancel);
        form.Children.Add(buttons);
        Content = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        _action.SelectionChanged += (_, _) => BuildSpecificFields();
        Loaded += (_, _) => { BuildSpecificFields(); _action.Focus(); };
    }

    public int TargetIndex => _targets.SelectedIndex;
    public int BatchSize => int.TryParse(_batch.Text.Trim(), out var value) && value > 0 ? value : 0;
    public SoftwareAction Action => (SoftwareAction)Math.Max(0, _action.SelectedIndex);
    public string Level => _level.SelectedIndex == 1 ? Levels.User : Levels.System;
    public string LocalFile => _file.Text.Trim();
    public string RemotePath => _remote.Text.Trim();
    public string Arguments => _arguments.Text.Trim();
    public string PackageId => _packageId.Text.Trim();
    public string RegistryView => _registryView.SelectedIndex switch { 1 => "64", 2 => "32", _ => "default" };

    void BuildSpecificFields()
    {
        // The dialog reuses the same input controls so values survive switching between
        // operation types. Clear their current logical parents first; clearing only the
        // outer StackPanel does not detach controls nested inside FileField's DockPanel.
        foreach (var control in new Control[] { _file, _remote, _arguments, _packageId, _registryView })
            Detach(control);
        _specific.Children.Clear();
        switch (Action)
        {
            case SoftwareAction.Exe:
                _fileFilter = "Executable installers (*.exe)|*.exe";
                if (_arguments.Text == "/qn /norestart") _arguments.Clear();
                _specific.Children.Add(FileField("EXE installer on this computer", _file));
                _specific.Children.Add(Field("...or an EXE path the student PC can reach", _remote));
                _specific.Children.Add(Field("EXE silent parameters (for example /S or /VERYSILENT)", _arguments));
                _specific.Children.Add(Note("EXE installers must provide their vendor-specific silent parameters. A visible setup window or elevation prompt cannot be answered remotely and will time out."));
                break;
            case SoftwareAction.Msi:
                _fileFilter = "Windows Installer packages (*.msi)|*.msi";
                if (_arguments.Text.Length == 0) _arguments.Text = "/qn /norestart";
                _specific.Children.Add(FileField("MSI package on this computer", _file));
                _specific.Children.Add(Field("...or an MSI path the student PC can reach", _remote));
                _specific.Children.Add(Field("MSI options and properties", _arguments));
                _specific.Children.Add(Note("The package is run with msiexec /i. Quiet, no-restart options are supplied by default; append vendor properties such as PROPERTY=value when required."));
                break;
            case SoftwareAction.WingetInstall:
            case SoftwareAction.WingetUninstall:
                _specific.Children.Add(Field("winget package _ID (for example VideoLAN.VLC)", _packageId));
                _specific.Children.Add(Note("The exact package ID is used with silent, non-interactive mode and the selected user or machine scope."));
                break;
            case SoftwareAction.Registry:
                _fileFilter = "Registry files (*.reg)|*.reg";
                _specific.Children.Add(FileField("Registry file on this computer", _file));
                _specific.Children.Add(Field("Registry _view", _registryView));
                _specific.Children.Add(Note("The .reg file is uploaded and applied with reg.exe import. Elevated mode can change machine settings; standard-user mode is limited to that user's registry. Registry changes have no automatic undo."));
                break;
        }
    }

    bool Validate()
    {
        if (_targets.SelectedIndex < 0) return Fail("Choose which computers should receive this action.", _targets);
        if (_batch.Text.Trim().Length > 0 && (!int.TryParse(_batch.Text.Trim(), out var batch) || batch < 1))
            return Fail("Enter a positive batch size, or leave it blank to run on all targets at once.", _batch);
        if (Action is SoftwareAction.WingetInstall or SoftwareAction.WingetUninstall)
            return _packageId.Text.Trim().Length > 0 || Fail("Enter the exact winget package ID.", _packageId);
        if (Action == SoftwareAction.Registry)
            return ValidLocalExtension(".reg", "Choose an existing .reg file.");

        var extension = Action == SoftwareAction.Exe ? ".exe" : ".msi";
        if (_remote.Text.Trim().Length > 0 && !System.IO.Path.GetExtension(_remote.Text.Trim()).Equals(extension, StringComparison.OrdinalIgnoreCase))
            return Fail($"The remote path must identify a {extension} file.", _remote);
        if (_remote.Text.Trim().Length == 0 && !ValidLocalExtension(extension, $"Choose an existing {extension} file, or enter a remote path."))
            return false;
        return true;
    }

    bool ValidLocalExtension(string extension, string message)
    {
        var path = _file.Text.Trim();
        return (System.IO.File.Exists(path) && System.IO.Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)) ||
               Fail(message, _file);
    }

    bool Fail(string message, Control control)
    {
        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        control.Focus();
        return false;
    }

    FrameworkElement FileField(string label, TextBox textBox)
    {
        var browse = new Button { Content = "_Browse...", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = _fileFilter, Title = label };
            if (picker.ShowDialog(this) == true) textBox.Text = picker.FileName;
        };
        var row = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(textBox);
        return Field(label, row, textBox);
    }

    static void Detach(Control control)
    {
        if (control.Parent is Panel panel)
            panel.Children.Remove(control);
        else if (control.Parent is ContentControl content && ReferenceEquals(content.Content, control))
            content.Content = null;
    }

    static FrameworkElement Field(string label, FrameworkElement control, Control? labelledControl = null)
    {
        var target = labelledControl ?? control as Control;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var prompt = new Label { Content = label, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 3), Target = target };
        if (target is not null) AutomationProperties.SetName(target, label.Replace("_", ""));
        panel.Children.Add(prompt);
        panel.Children.Add(control);
        return panel;
    }

    static TextBlock Note(string text) => Ui.MutedText(text, new Thickness(0, 0, 0, 8));
}
