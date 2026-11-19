// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ClassroomManager.Host;

public enum WallpaperChoiceKind { Image, SolidColor, WindowsDefault }

sealed class WallpaperItem
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Details
    {
        get
        {
            try
            {
                var info = new FileInfo(Path);
                return $"{Name}  ({info.Length / 1024.0 / 1024.0:0.0} MB)";
            }
            catch { return Name; }
        }
    }
}

/// <summary>A local, persistent wallpaper library for the Host with image preview and explicit removal.</summary>
public sealed class WallpaperLibraryWindow : Window
{
    static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp"];
    static string LibraryDirectory => System.IO.Path.Combine(HostSettings.DataFolder, "Wallpapers");

    readonly ListBox _list = new() { MinWidth = 260, DisplayMemberPath = nameof(WallpaperItem.Details) };
    readonly Image _preview = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _previewStatus = Ui.StatusText(new Thickness(0));
    readonly ComboBox _style = new() { ItemsSource = new[] { "Fill", "Fit", "Stretch", "Tile", "Center", "Span" }, SelectedIndex = 0, Width = 150 };
    readonly ComboBox _target = new() { ItemsSource = WallpaperTargets.Labels, SelectedIndex = 0, Width = 210 };
    public const string AllUsers = "All users of the computer";
    public const string SignedInUser = "Only the signed-in user";
    readonly ComboBox _scope = new() { ItemsSource = new[] { AllUsers, SignedInUser }, SelectedIndex = 0, Width = 190, IsEnabled = false };
    readonly Button _apply = new() { Content = "_Use selected image", ToolTip = "Use selected image — upload and apply this library image to the selected computers.", IsDefault = true, IsEnabled = false };

    public string SelectedFile { get; private set; } = "";
    public string SelectedStyle => _style.SelectedItem as string ?? "Fill";
    public string SelectedColor { get; private set; } = "0078D4";
    public WallpaperChoiceKind SelectedKind { get; private set; } = WallpaperChoiceKind.Image;
    public WallpaperTarget SelectedTarget => WallpaperTargets.FromLabel(_target.SelectedItem as string);
    /// <summary>The lock screen picture for the signed-in user only (every edition), not for all users (Pro and up).</summary>
    public bool SignInForSignedInUserOnly => SelectedTarget.IncludesSignIn() && _scope.SelectedItem as string == SignedInUser;

    public WallpaperLibraryWindow(Window owner)
    {
        Owner = owner;
        Title = "Wallpaper and background";
        Width = 920;
        Height = 620;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var add = new Button { Content = "_Add images...", ToolTip = "Add images — copy JPG, PNG or BMP files into this app's wallpaper library." };
        add.Click += (_, _) => AddImages();
        var remove = new Button { Content = "_Remove selected", ToolTip = "Remove selected — delete the chosen copy from the app library only." };
        remove.Click += (_, _) => RemoveSelected();
        var refresh = new Button { Content = "Re_fresh", ToolTip = "Refresh (F5) — rescan the wallpaper library folder." };
        refresh.Click += (_, _) => LoadLibrary();

        var leftButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        leftButtons.Children.Add(add);
        leftButtons.Children.Add(remove);
        leftButtons.Children.Add(refresh);
        var left = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(leftButtons);
        left.Children.Add(_list);

        var previewBorder = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            MinHeight = 320,
            Child = _preview,
        };
        previewBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrushColor");
        _target.ToolTip = "Desktop wallpaper changes the signed-in user's desktop. The lock and sign-in screen picture is one setting for every user of the computer.";
        var styleRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        styleRow.Children.Add(Ui.Label("Apply _to:", _target, new Thickness(0, 0, 8, 0)));
        styleRow.Children.Add(_target);
        _scope.ToolTip = "Whose lock screen: one picture for every user of the computer (not on Windows Home), or the signed-in user's own (also on Windows Home).";
        _target.SelectionChanged += (_, _) => _scope.IsEnabled = SelectedTarget.IncludesSignIn();
        styleRow.Children.Add(Ui.Label("_For:", _scope, new Thickness(12, 0, 8, 0)));
        styleRow.Children.Add(_scope);
        styleRow.Children.Add(Ui.Label("Display _style:", _style, new Thickness(12, 0, 8, 0)));
        styleRow.Children.Add(_style);
        styleRow.Children.Add(_apply);
        var backgroundChoices = BackgroundChoices();
        var right = new DockPanel();
        DockPanel.SetDock(_previewStatus, Dock.Bottom);
        DockPanel.SetDock(backgroundChoices, Dock.Bottom);
        DockPanel.SetDock(styleRow, Dock.Bottom);
        right.Children.Add(_previewStatus);
        right.Children.Add(backgroundChoices);
        right.Children.Add(styleRow);
        right.Children.Add(previewBorder);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        body.Children.Add(left);
        body.Children.Add(right);

        Content = new Border { Padding = new Thickness(12), Child = body };
        _list.SelectionChanged += (_, _) => ShowPreview();
        _list.MouseDoubleClick += (_, e) => { if (e.ChangedButton == MouseButton.Left && _apply.IsEnabled) Choose(); };
        _apply.Click += (_, _) => Choose();
        Loaded += (_, _) => LoadLibrary();
        Ui.RefreshOnF5(this, () => { LoadLibrary(); return Task.CompletedTask; });
    }

    void LoadLibrary(string? selectPath = null)
    {
        List<WallpaperItem> items;
        try
        {
            Directory.CreateDirectory(LibraryDirectory);
            items = Directory.EnumerateFiles(LibraryDirectory)
                .Where(p => Extensions.Contains(System.IO.Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                .OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(p => new WallpaperItem { Path = p }).ToList();
        }
        catch (Exception ex)
        {
            _previewStatus.Text = "Could not open the wallpaper library: " + ex.Message;
            _apply.IsEnabled = false;
            return;
        }
        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(i => selectPath is not null && i.Path.Equals(selectPath, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault();
        if (items.Count == 0)
        {
            _preview.Source = null;
            _previewStatus.Text = "The library is empty. Choose Add images to copy wallpapers into the app library.";
            _apply.IsEnabled = false;
        }
    }

    void AddImages()
    {
        var picker = new OpenFileDialog
        {
            Title = "Add wallpaper images to the library",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
            Multiselect = true,
        };
        if (picker.ShowDialog(this) != true) return;
        string? last = null;
        var failures = new List<string>();
        foreach (var source in picker.FileNames)
        {
            try
            {
                var destination = UniqueDestination(System.IO.Path.GetFileName(source));
                File.Copy(source, destination);
                last = destination;
            }
            catch (Exception ex) { failures.Add($"{System.IO.Path.GetFileName(source)}: {ex.Message}"); }
        }
        LoadLibrary(last);
        if (failures.Count > 0)
            MessageBox.Show(this, string.Join("\n\n", failures), "Some wallpapers could not be added", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    static string UniqueDestination(string fileName)
    {
        var destination = System.IO.Path.Combine(LibraryDirectory, fileName);
        if (!File.Exists(destination)) return destination;
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var ext = System.IO.Path.GetExtension(fileName);
        for (int n = 2; ; n++)
        {
            destination = System.IO.Path.Combine(LibraryDirectory, $"{stem} ({n}){ext}");
            if (!File.Exists(destination)) return destination;
        }
    }

    void RemoveSelected()
    {
        if (_list.SelectedItem is not WallpaperItem item) return;
        if (MessageBox.Show(this, $"Remove this image from the app library?\n\n{item.Name}\n\nThe original source file is not affected.",
                "Remove wallpaper", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { File.Delete(item.Path); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not remove wallpaper", MessageBoxButton.OK, MessageBoxImage.Warning); }
        LoadLibrary();
    }

    void ShowPreview()
    {
        _preview.Source = null;
        _apply.IsEnabled = false;
        if (_list.SelectedItem is not WallpaperItem item) return;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(item.Path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            _preview.Source = image;
            _previewStatus.Text = $"{item.Name} — {image.PixelWidth} × {image.PixelHeight} pixels";
            _apply.IsEnabled = true;
        }
        catch (Exception ex) { _previewStatus.Text = "Preview failed: " + ex.Message; }
    }

    void Choose()
    {
        if (_list.SelectedItem is not WallpaperItem item || !File.Exists(item.Path)) return;
        SelectedKind = WallpaperChoiceKind.Image;
        SelectedFile = item.Path;
        DialogResult = true;
    }

    FrameworkElement BackgroundChoices()
    {
        var colors = new (string Name, string Hex)[]
        {
            ("Dark gray", "2D2D30"), ("Gray", "767676"), ("White", "FFFFFF"),
            ("Red", "C42B1C"), ("Orange", "CA5010"), ("Gold", "F9A825"),
            ("Green", "107C10"), ("Teal", "008272"), ("Windows blue", "0078D4"),
            ("Dark blue", "004E8C"), ("Purple", "5C2D91"), ("Pink", "E3008C"),
        };
        var swatches = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        var useColor = new Button
        {
            Content = "Use selected _solid color",
            ToolTip = "Use solid color — remove the wallpaper image and use the selected Windows desktop color.",
        };
        foreach (var color in colors)
        {
            var swatch = new Button
            {
                Width = 34,
                Height = 34,
                MinHeight = 34,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 6),
                Background = (Brush)new BrushConverter().ConvertFromString("#" + color.Hex)!,
                ToolTip = $"{color.Name} — #{color.Hex}",
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, $"Select {color.Name} solid color");
            swatch.Click += (_, _) =>
            {
                SelectedColor = color.Hex;
                _previewStatus.Text = $"Selected solid color: {color.Name} (#{color.Hex})";
            };
            swatches.Children.Add(swatch);
        }
        useColor.Click += (_, _) =>
        {
            if (SelectedTarget.IncludesSignIn())
            {
                MessageBox.Show(this,
                    "Windows has no solid-color setting for the lock and sign-in screen. Set Apply to: Desktop wallpaper to use a solid color.",
                    "Solid color", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SelectedKind = WallpaperChoiceKind.SolidColor;
            DialogResult = true;
        };
        var useDefault = new Button
        {
            Content = "Use _Windows default wallpaper",
            ToolTip = "Windows default — use the stock wallpaper installed with Windows, not the previously saved user wallpaper. For the sign-in screen, removes the managed picture so Windows shows its own again.",
        };
        useDefault.Click += (_, _) =>
        {
            SelectedKind = WallpaperChoiceKind.WindowsDefault;
            DialogResult = true;
        };
        var actions = new WrapPanel();
        actions.Children.Add(useColor);
        actions.Children.Add(useDefault);
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Solid color", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(swatches);
        panel.Children.Add(actions);
        return panel;
    }
}
