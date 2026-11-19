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

/// <summary>One picture of a computer's "Recent images" row, as the Recent wallpapers window lists it.</summary>
public sealed class RecentWallpaperEntry
{
    public required ClientInfo Client { get; init; }
    public required WallpaperHistory.Image Image { get; init; }
    public ImageSource? Preview { get; init; }
    public string Computer => Client.Name;
    public string User => Client.User;
    public string FileName => Path.GetFileName(Image.Path);
    public string Folder => Path.GetDirectoryName(Image.Path) ?? "";
    public string SizeText => !Image.Exists ? "" : Image.Size < 1048576 ? $"{Image.Size / 1024.0:0} KB" : $"{Image.Size / 1048576.0:0.0} MB";
    public string Status => Image.Current ? "On the desktop now" : Image.Exists ? "" : "File no longer there";
}

/// <summary>
/// The "Recent images" that Windows offers under Settings > Personalization > Background on the selected
/// computers: see them with a preview, take them off the list and, if wanted, delete the student's own files.
/// </summary>
public sealed class RecentWallpapersWindow : Window
{
    readonly IReadOnlyList<ClientInfo> _targets;
    readonly HostSettings _settings;
    readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxHeight = 200 };
    readonly TextBlock _status = Ui.StatusText();
    readonly CheckBox _deleteFiles = new()
    {
        Content = "Also _delete the picture files (only the student's own files; never the one on the desktop now)",
        Margin = new Thickness(0, 8, 0, 0),
    };

    public RecentWallpapersWindow(List<ClientInfo> targets, HostSettings settings, Window owner)
    {
        _targets = targets;
        _settings = settings;
        Owner = owner;
        Title = targets.Count == 1 ? $"Recent wallpapers — {targets[0].Name}" : $"Recent wallpapers — {targets.Count} computers";
        Width = 900;
        Height = 560;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var columns = new GridView();
        columns.Columns.Add(Column("Computer", nameof(RecentWallpaperEntry.Computer), 120));
        columns.Columns.Add(Column("User", nameof(RecentWallpaperEntry.User), 110));
        columns.Columns.Add(Column("Picture", nameof(RecentWallpaperEntry.FileName), 200));
        columns.Columns.Add(Column("Folder", nameof(RecentWallpaperEntry.Folder), 230));
        columns.Columns.Add(Column("Size", nameof(RecentWallpaperEntry.SizeText), 70));
        columns.Columns.Add(Column("Status", nameof(RecentWallpaperEntry.Status), 140));
        _list.View = columns;
        _list.SelectionChanged += (_, _) => _preview.Source = (_list.SelectedItem as RecentWallpaperEntry)?.Preview;

        var refresh = new Button { Content = "_Refresh", Tag = FluentIcons.Refresh, ToolTip = "Refresh (F5) — read the recent images again from the selected computers." };
        refresh.Click += async (_, _) => await LoadAsync();
        var remove = new Button { Content = "Re_move from Recent images...", Tag = FluentIcons.Delete, ToolTip = "Remove — take the selected pictures off the Recent images list in the students' Background settings." };
        remove.Click += async (_, _) => await RemoveAsync();
        var close = new Button { Content = "_Close", Tag = FluentIcons.Close, IsCancel = true };
        close.Click += (_, _) => Close();
        var buttons = Ui.Spaced(new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { refresh, remove, close } });

        var note = Ui.Explanation(
            "Windows shows the last five background pictures of each user under Settings > Personalization > Background > Recent images. " +
            "Removing a picture takes it off that list; the desktop keeps what it shows now.",
            new Thickness(0, 0, 0, 8));
        var previewBorder = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0, 8, 0, 0), Child = _preview, MinHeight = 60 };
        previewBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrushColor");

        var bottom = new StackPanel();
        bottom.Children.Add(previewBorder);
        bottom.Children.Add(_deleteFiles);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(note);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
        Ui.RefreshOnF5(this, LoadAsync);
    }

    static GridViewColumn Column(string header, string path, double width) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new Binding(path) };

    async Task LoadAsync()
    {
        _status.Text = $"Reading recent images from {_targets.Count} computer(s)...";
        var answers = await Task.WhenAll(_targets.Select(async client =>
        {
            try
            {
                using var session = await ClientSession.OpenAsync(client, _settings);
                var images = WallpaperHistory.ImagesFrom(await session.CallAtAsync(Levels.User, Cmd.WallpaperRecentList));
                return (Client: client, Entries: images.Select(image => new RecentWallpaperEntry { Client = client, Image = image, Preview = Decode(image.Preview) }).ToList(), Error: "");
            }
            catch (Exception ex) { return (Client: client, Entries: new List<RecentWallpaperEntry>(), Error: ex.Message); }
        }));
        _list.ItemsSource = answers.SelectMany(answer => answer.Entries).ToList();
        var failed = answers.Where(answer => answer.Error.Length > 0).ToList();
        var count = answers.Sum(answer => answer.Entries.Count);
        var notRead = string.Join("; ", failed.Select(answer => $"{answer.Client.Name}: {answer.Error}"));
        _status.Text = failed.Count == 0
            ? $"{count} recent image(s) on {answers.Length} computer(s)."
            : $"{count} recent image(s) on {answers.Length - failed.Count} computer(s). Not read: {notRead}";
    }

    static ImageSource? Decode(string base64)
    {
        if (base64.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException) { return null; }
    }

    async Task RemoveAsync()
    {
        var selected = _list.SelectedItems.Cast<RecentWallpaperEntry>().ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "Select one or more pictures first."); return; }
        var deleteFiles = _deleteFiles.IsChecked == true;
        var question = deleteFiles
            ? $"Remove {selected.Count} picture(s) from Recent images and delete their files? Deleted files cannot be restored."
            : $"Remove {selected.Count} picture(s) from Recent images? The files themselves stay where they are.";
        if (MessageBox.Show(this, question, "Recent wallpapers", MessageBoxButton.OKCancel,
                deleteFiles ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var results = await Task.WhenAll(selected.GroupBy(entry => entry.Client).Select(async group =>
        {
            try
            {
                using var session = await ClientSession.OpenAsync(group.Key, _settings);
                var data = await session.CallAtAsync(Levels.User, Cmd.WallpaperRecentRemove, new JsonObject
                {
                    [WallpaperHistory.Paths] = new JsonArray(group.Select(entry => (JsonNode)entry.Image.Path).ToArray()),
                    [WallpaperHistory.DeleteFiles] = deleteFiles,
                });
                return $"{group.Key.Name}: {WallpaperHistory.Describe(data)}";
            }
            catch (Exception ex) { return $"{group.Key.Name}: {ex.Message}"; }
        }));
        await LoadAsync();
        _status.Text = string.Join("\n", results);
    }
}
