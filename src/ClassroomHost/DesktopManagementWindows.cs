// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed class ManagedWallpaperEntry
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public string Modified { get; init; } = "";
    public bool Current { get; init; }
    public ImageSource? Preview { get; set; }
    public string SizeText => Size < 1024 ? $"{Size} B" : Size < 1048576 ? $"{Size / 1024.0:0.0} KB" : $"{Size / 1048576.0:0.0} MB";
    public string CurrentText => Current ? "In use" : "";
}

public sealed class ManagedWallpapersWindow : Window
{
    ClientInfo _client;
    readonly HostSettings _settings;
    readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    readonly TextBlock _status = Ui.StatusText();
    readonly ComboBox _style = new()
    {
        ItemsSource = new[] { "Fill", "Fit", "Stretch", "Tile", "Center", "Span" },
        SelectedIndex = 0,
        Width = 120,
    };
    readonly Button _apply = new()
    {
        Content = "_Apply selected",
        ToolTip = "Apply selected — use the selected image as this student's wallpaper without uploading it again.",
        IsEnabled = false,
    };

    public ManagedWallpapersWindow(ClientInfo client, HostSettings settings, Window owner)
    {
        _client = client;
        _settings = settings;
        Owner = owner;
        Title = $"Managed wallpapers — {client.Name}";
        Width = 760;
        Height = 500;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var columns = new GridView();
        columns.Columns.Add(Column("Name", nameof(ManagedWallpaperEntry.Name), 330));
        columns.Columns.Add(Column("Size", nameof(ManagedWallpaperEntry.SizeText), 100));
        columns.Columns.Add(Column("Modified (UTC)", nameof(ManagedWallpaperEntry.Modified), 170));
        columns.Columns.Add(Column("Status", nameof(ManagedWallpaperEntry.CurrentText), 90));
        _list.View = columns;
        var previewTemplate = new DataTemplate();
        var previewImage = new FrameworkElementFactory(typeof(Image));
        previewImage.SetBinding(Image.SourceProperty, new Binding(nameof(ManagedWallpaperEntry.Preview)));
        previewImage.SetValue(FrameworkElement.MaxWidthProperty, 360d);
        previewImage.SetValue(FrameworkElement.MaxHeightProperty, 240d);
        previewImage.SetValue(Image.StretchProperty, Stretch.Uniform);
        previewTemplate.VisualTree = previewImage;
        var itemStyle = new Style(typeof(ListViewItem));
        var tooltip = new ToolTip { ContentTemplate = previewTemplate, Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
        tooltip.SetBinding(ContentControl.ContentProperty, new Binding());
        itemStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, tooltip));
        itemStyle.Setters.Add(new Setter(ToolTipService.InitialShowDelayProperty, 250));
        itemStyle.Setters.Add(new Setter(ToolTipService.ShowDurationProperty, 30000));
        _list.ItemContainerStyle = itemStyle;

        var refresh = new Button { Content = "_Refresh", ToolTip = "Refresh — reload managed wallpaper images from this computer." };
        refresh.Click += async (_, _) => await LoadAsync();
        _apply.Click += async (_, _) => await ApplyAsync();
        var delete = new Button { Content = "_Delete selected...", ToolTip = "Delete selected — remove selected Classroom Manager wallpaper copies that are not currently in use." };
        delete.Click += async (_, _) => await DeleteAsync();
        var close = new Button { Content = "_Close", IsCancel = true };
        close.Click += (_, _) => Close();
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(refresh);
        buttons.Children.Add(Ui.Label("Display _style:", _style, new Thickness(8, 0, 6, 0)));
        buttons.Children.Add(_style);
        buttons.Children.Add(_apply);
        Ui.Spaced(buttons);
        buttons.Children.Add(delete);
        buttons.Children.Add(close);

        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in ComputerPicker.ForRow(new ComputerPicker(client, SwitchTo))) top.Children.Add(item);
        var note = Ui.Explanation(
            "These are copies that Classroom Manager placed on this signed-in user's computer. Select one to apply it remotely without uploading it again. The current wallpaper is marked In use and cannot be deleted until another background is applied.",
            new Thickness(0, 0, 0, 8));
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;
        _list.SelectionChanged += (_, _) => UpdateApplyState();
        _list.MouseDoubleClick += async (_, _) =>
        {
            if (_apply.IsEnabled) await ApplyAsync();
        };
        Loaded += async (_, _) => await LoadAsync();
        Ui.RefreshOnF5(this, LoadAsync);
    }

    static GridViewColumn Column(string header, string path, double width) => new()
    {
        Header = header,
        Width = width,
        DisplayMemberBinding = new Binding(path),
    };

    /// <summary>The computer was changed in the picker: show it, with an empty list until its images arrive.</summary>
    async void SwitchTo(ClientInfo computer)
    {
        _client = computer;
        Title = $"Managed wallpapers — {computer.Name}";
        _list.ItemsSource = new List<ManagedWallpaperEntry>();
        UpdateApplyState();
        _status.Text = ComputerPicker.NowShowing(computer);
        await LoadAsync();
    }

    async Task LoadAsync()
    {
        var computer = _client;
        try
        {
            _status.Text = "Loading managed wallpapers...";
            using var session = await ClientSession.OpenAsync(computer, _settings);
            var data = await session.CallAtAsync(Levels.User, Cmd.WallpaperManagedList);
            var entries = data["wallpapers"]!.AsArray().Select(item => new ManagedWallpaperEntry
            {
                Name = item!["name"]!.GetValue<string>(),
                Size = item["size"]!.GetValue<long>(),
                Modified = item["modifiedUtc"]!.GetValue<string>(),
                Current = item["current"]!.GetValue<bool>(),
            }).ToList();
            if (_client.SupportsCommand(Cmd.WallpaperManagedPreview))
            {
                foreach (var entry in entries)
                {
                    try
                    {
                        var preview = await session.CallAtAsync(Levels.User, Cmd.WallpaperManagedPreview,
                            new JsonObject { ["name"] = entry.Name });
                        entry.Preview = DecodeImage(preview["data"]?.GetValue<string>() ?? "");
                    }
                    catch { /* one unreadable image should not hide the rest of the managed list */ }
                }
            }
            if (computer != _client) return;   // another computer was chosen meanwhile
            Ui.ReplaceItemsPreservingSelection(_list, entries, entry => entry.Name);
            _status.Text = entries.Count == 0
                ? "No Classroom Manager wallpaper images are stored on this computer."
                : $"{entries.Count} managed wallpaper image(s) on {_client.Name}.";
            UpdateApplyState();
        }
        catch (Exception ex) { if (computer == _client) _status.Text = "Could not load wallpapers: " + ex.Message; }
    }

    static ImageSource? DecodeImage(string base64)
    {
        if (base64.Length == 0) return null;
        using var stream = new MemoryStream(Convert.FromBase64String(base64));
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    void UpdateApplyState()
    {
        var supported = _client.SupportsCommand(Cmd.WallpaperManagedSet);
        _apply.IsEnabled = supported && _list.SelectedItems.Count == 1;
        _apply.ToolTip = supported
            ? "Apply selected — use the selected image as this student's wallpaper without uploading it again."
            : $"Applying an existing remote wallpaper requires client protocol {Protocol.RequiredVersion(Cmd.WallpaperManagedSet)}. Update this client first.";
    }

    async Task ApplyAsync()
    {
        if (_list.SelectedItems.Count != 1 || _list.SelectedItem is not ManagedWallpaperEntry selected) return;
        var style = _style.SelectedItem as string ?? "Fill";
        if (MessageBox.Show(this,
                $"Apply “{selected.Name}” to the signed-in user on {_client.Name} using {style.ToLowerInvariant()} display style?",
                "Apply managed wallpaper", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        try
        {
            _status.Text = $"Applying {selected.Name}...";
            using var session = await ClientSession.OpenAsync(_client, _settings);
            await session.CallAtAsync(Levels.User, Cmd.WallpaperManagedSet, new JsonObject
            {
                ["name"] = selected.Name,
                ["style"] = style.ToLowerInvariant(),
            });
            await LoadAsync();
            _status.Text = $"Applied {selected.Name} on {_client.Name}.";
        }
        catch (Exception ex) { _status.Text = "Could not apply wallpaper: " + ex.Message; }
    }

    async Task DeleteAsync()
    {
        var selected = _list.SelectedItems.Cast<ManagedWallpaperEntry>().ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "Select one or more wallpaper images first."); return; }
        if (selected.Any(item => item.Current))
        {
            MessageBox.Show(this, "The current wallpaper cannot be deleted. Apply another image, color, or Windows default first.",
                "Wallpaper in use", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"Delete {selected.Count} managed wallpaper image(s) from {_client.Name}?\n\nThis does not delete images in the Host wallpaper library.",
                "Delete managed wallpapers", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            using var session = await ClientSession.OpenAsync(_client, _settings);
            var names = new JsonArray(selected.Select(item => (JsonNode)item.Name).ToArray());
            var result = await session.CallAtAsync(Levels.User, Cmd.WallpaperManagedDelete,
                new JsonObject { ["names"] = names });
            _status.Text = $"Deleted {result["deleted"]?.GetValue<int>() ?? 0} wallpaper image(s).";
            await LoadAsync();
        }
        catch (Exception ex) { _status.Text = "Could not delete wallpapers: " + ex.Message; }
    }
}

public sealed class WindowsThemeEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Location { get; init; } = "";
    public bool Current { get; init; }
    public string CurrentText => Current ? "Current" : "";
}

public sealed class WindowsThemesWindow : Window
{
    ClientInfo _client;
    readonly HostSettings _settings;
    readonly ListView _list = new() { SelectionMode = SelectionMode.Single };
    readonly TextBlock _status = Ui.StatusText();

    public WindowsThemesWindow(ClientInfo client, HostSettings settings, Window owner)
    {
        _client = client;
        _settings = settings;
        Owner = owner;
        Title = $"Windows themes — {client.Name}";
        Width = 680;
        Height = 470;
        MinWidth = 520;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var columns = new GridView();
        columns.Columns.Add(Column("Theme", nameof(WindowsThemeEntry.Name), 300));
        columns.Columns.Add(Column("Source", nameof(WindowsThemeEntry.Location), 150));
        columns.Columns.Add(Column("Status", nameof(WindowsThemeEntry.CurrentText), 100));
        _list.View = columns;

        var refresh = new Button { Content = "_Refresh" };
        refresh.Click += async (_, _) => await LoadAsync();
        var apply = new Button { Content = "_Apply selected theme..." };
        apply.Click += async (_, _) => await ApplyAsync();
        var close = new Button { Content = "_Close", IsCancel = true };
        close.Click += (_, _) => Close();
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var item in ComputerPicker.ForRow(new ComputerPicker(client, SwitchTo))) top.Children.Add(item);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(refresh);
        buttons.Children.Add(apply);
        buttons.Children.Add(close);
        var note = Ui.Explanation(
            "Windows default light and dark reset the base theme and color mode. Other entries are installed theme files; optional themes such as Glow appear only when present on that computer. Applying a theme may briefly open Personalization.",
            new Thickness(0, 0, 0, 8));
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(note);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
        Ui.RefreshOnF5(this, LoadAsync);
    }

    static GridViewColumn Column(string header, string path, double width) => new()
    {
        Header = header,
        Width = width,
        DisplayMemberBinding = new Binding(path),
    };

    /// <summary>The computer was changed in the picker: show it, with an empty list until its themes arrive.</summary>
    async void SwitchTo(ClientInfo computer)
    {
        _client = computer;
        Title = $"Windows themes — {computer.Name}";
        _list.ItemsSource = new List<WindowsThemeEntry>();
        _status.Text = ComputerPicker.NowShowing(computer);
        await LoadAsync();
    }

    async Task LoadAsync()
    {
        var computer = _client;
        try
        {
            _status.Text = "Loading Windows themes...";
            using var session = await ClientSession.OpenAsync(computer, _settings);
            var data = await session.CallAtAsync(Levels.User, Cmd.WindowsThemesList);
            if (computer != _client) return;   // another computer was chosen meanwhile
            var entries = data["themes"]!.AsArray().Select(item => new WindowsThemeEntry
            {
                Id = item!["id"]!.GetValue<string>(),
                Name = item["name"]!.GetValue<string>(),
                Location = item["location"]!.GetValue<string>(),
                Current = item["current"]!.GetValue<bool>(),
            }).ToList();
            _list.ItemsSource = entries;
            _list.SelectedItem = entries.FirstOrDefault(item => item.Current) ?? entries.FirstOrDefault();
            _status.Text = entries.Count == 0 ? "Windows reported no available theme files." : $"{entries.Count} available theme(s).";
        }
        catch (Exception ex) { if (computer == _client) _status.Text = "Could not load themes: " + ex.Message; }
    }

    async Task ApplyAsync()
    {
        if (_list.SelectedItem is not WindowsThemeEntry theme)
        {
            MessageBox.Show(this, "Select a Windows theme first.");
            return;
        }
        if (MessageBox.Show(this, $"Apply “{theme.Name}” to the signed-in user on {_client.Name}?",
                "Apply Windows theme", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        try
        {
            using var session = await ClientSession.OpenAsync(_client, _settings);
            await session.CallAtAsync(Levels.User, Cmd.WindowsThemeSet, new JsonObject { ["id"] = theme.Id });
            _status.Text = $"Windows was asked to apply {theme.Name}. Refresh after the theme finishes changing.";
        }
        catch (Exception ex) { _status.Text = "Could not apply theme: " + ex.Message; }
    }
}
