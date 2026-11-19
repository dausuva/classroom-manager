// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Wallpaper and mouse settings. These apply to the user the client runs as.</summary>
sealed class DesktopHandler
{
    // ---- Win32 ------------------------------------------------------------
    const uint SPI_SETMOUSE = 0x0004;
    const uint SPI_SETDOUBLECLICKTIME = 0x0020;
    const uint SPI_SETMOUSEBUTTONSWAP = 0x0021;
    const uint SPI_SETDESKWALLPAPER = 0x0014;
    const uint SPI_GETDESKWALLPAPER = 0x0073;
    const uint SPI_SETMOUSESPEED = 0x0071;
    const uint SPI_SETWHEELSCROLLLINES = 0x0069;
    const uint SPIF_UPDATEINIFILE = 0x01;
    const uint SPIF_SENDCHANGE = 0x02;
    const uint Persist = SPIF_UPDATEINIFILE | SPIF_SENDCHANGE;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfo(uint action, uint param, string? vparam, uint winIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfo(uint action, uint param, StringBuilder vparam, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, IntPtr vparam, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, int[] vparam, uint winIni);

    // ---- Wallpaper ----------------------------------------------------------

    sealed class WallpaperState
    {
        public string? OriginalPath { get; set; }
        public string? OriginalStyle { get; set; }
        public string? OriginalTile { get; set; }
        public string? OriginalColor { get; set; }
    }

    static string StatePath => Path.Combine(ClientConfig.UserDir, "wallpaper-state.json");
    static string ManagedDirectory => Path.Combine(ClientConfig.UserDir, "Wallpapers");
    const string DesktopKey = @"Control Panel\Desktop";

    public Response SetWallpaper(JsonObject a)
    {
        var path = a.Str("path");
        var style = a.Str("style", "fill").ToLowerInvariant();
        if (!File.Exists(path)) return Response.Fail($"Image not found: {path}");

        SaveOriginalWallpaper();

        var (styleVal, tileVal) = WallpaperStyle(style);
        string managedPath;
        try { managedPath = StoreManagedWallpaper(path, a.Str("name", Path.GetFileName(path))); }
        catch (Exception ex) { return Response.Fail("Could not store the managed wallpaper: " + ex.Message); }
        return Apply(managedPath, styleVal, tileVal);
    }

    public Response SetDefaultWallpaper()
    {
        SaveOriginalWallpaper();
        var path = DefaultWallpaper();
        return path.Length == 0
            ? Response.Fail("Windows default wallpaper was not found on this computer.")
            : Apply(path, "10", "0");
    }

    public Response SetSolidColor(JsonObject a)
    {
        var color = a.Str("color").Trim().TrimStart('#');
        if (color.Length != 6 || !int.TryParse(color, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            return Response.Fail("Solid color must be a six-digit RGB value such as 0078D4.");

        SaveOriginalWallpaper();
        using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Colors"))
            key.SetValue("Background", $"{(rgb >> 16) & 255} {(rgb >> 8) & 255} {rgb & 255}", RegistryValueKind.String);
        return Apply("", "0", "0");
    }

    public Response ListManagedWallpapers()
    {
        try
        {
            Directory.CreateDirectory(ManagedDirectory);
            var currentPath = CurrentWallpaper();
            var current = currentPath.Length == 0 ? "" : Path.GetFullPath(currentPath);
            var items = Directory.EnumerateFiles(ManagedDirectory)
                .Select(path => new FileInfo(path))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Select(file => new JsonObject
                {
                    ["name"] = file.Name,
                    ["size"] = file.Length,
                    ["modifiedUtc"] = file.LastWriteTimeUtc.ToString("O"),
                    ["current"] = file.FullName.Equals(current, StringComparison.OrdinalIgnoreCase),
                }).ToArray();
            return Response.Success(new JsonObject
            {
                ["wallpapers"] = new JsonArray(items.Cast<JsonNode>().ToArray()),
                ["currentPath"] = currentPath,
            });
        }
        catch (Exception ex) { return Response.Fail("Could not list managed wallpapers: " + ex.Message); }
    }

    public Response SetManagedWallpaper(JsonObject a)
    {
        try
        {
            Directory.CreateDirectory(ManagedDirectory);
            var name = a.Str("name");
            if (name.Length == 0 || Path.GetFileName(name) != name)
                return Response.Fail("A managed wallpaper name is invalid.");

            var root = Path.GetFullPath(ManagedDirectory) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(ManagedDirectory, name));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return Response.Fail("The managed wallpaper path is outside the managed folder.");
            if (!File.Exists(path)) return Response.Fail($"Managed wallpaper not found: {name}");

            SaveOriginalWallpaper();
            var (style, tile) = WallpaperStyle(a.Str("style", "fill"));
            return Apply(path, style, tile);
        }
        catch (Exception ex) { return Response.Fail("Could not apply the managed wallpaper: " + ex.Message); }
    }

    public Response PreviewManagedWallpaper(JsonObject a)
    {
        try
        {
            var path = ManagedWallpaperPath(a.Str("name"));
            if (!File.Exists(path)) return Response.Fail("The managed wallpaper was not found.");
            var (data, width, height) = Thumbnail(path, 360, 240);
            return Response.Success(new JsonObject
            {
                ["mime"] = "image/jpeg",
                ["data"] = Convert.ToBase64String(data),
                ["width"] = width,
                ["height"] = height,
            });
        }
        catch (Exception ex) { return Response.Fail("Could not create the wallpaper preview: " + ex.Message); }
    }

    /// <summary>A JPEG no larger than the given size, for showing an image on the Host.</summary>
    static (byte[] Data, int Width, int Height) Thumbnail(string path, int maxWidth, int maxHeight)
    {
        using var source = System.Drawing.Image.FromFile(path);
        var scale = Math.Min(1d, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var thumbnail = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(thumbnail))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, 0, 0, width, height);
        }
        using var output = new MemoryStream();
        thumbnail.Save(output, System.Drawing.Imaging.ImageFormat.Jpeg);
        return (output.ToArray(), width, height);
    }

    // ---- Recent images of the Background settings (see WallpaperHistory) ------------------------

    static List<string> ReadHistory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WallpaperHistory.Key);
        return Enumerable.Range(0, WallpaperHistory.Slots)
            .Select(slot => key?.GetValue(WallpaperHistory.ValuePrefix + slot) as string ?? "")
            .Where(path => path.Trim().Length > 0).ToList();
    }

    /// <summary>Whether the desktop shows this picture now, by where Windows recorded it came from.</summary>
    static bool IsCurrentWallpaper(string path)
    {
        using var desktop = Registry.CurrentUser.OpenSubKey(DesktopKey);
        return new[] { desktop?.GetValue("WallPaper") as string ?? "", CurrentWallpaper() }
            .Any(current => current.Length > 0 && current.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    public Response ListRecentWallpapers()
    {
        try
        {
            var images = ReadHistory().Select(path =>
            {
                var file = new FileInfo(path);
                var preview = "";
                if (file.Exists)
                    try { preview = Convert.ToBase64String(Thumbnail(path, 240, 160).Data); }
                    catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or IOException or UnauthorizedAccessException) { }
                return (JsonNode)new WallpaperHistory.Image(path, file.Exists, file.Exists ? file.Length : 0, IsCurrentWallpaper(path), preview).ToJson();
            }).ToArray();
            return Response.Success(new JsonObject { [WallpaperHistory.Images] = new JsonArray(images) });
        }
        catch (Exception ex) { return Response.Fail("Could not read the recent wallpaper images: " + ex.Message); }
    }

    public Response RemoveRecentWallpapers(JsonObject a)
    {
        try
        {
            var remove = (a[WallpaperHistory.Paths] as JsonArray ?? []).Select(node => node?.GetValue<string>() ?? "").Where(path => path.Length > 0).ToList();
            if (remove.Count == 0) return Response.Fail("Choose at least one recent image.");
            var history = ReadHistory();
            var kept = WallpaperHistory.Without(history, remove);
            using (var key = Registry.CurrentUser.CreateSubKey(WallpaperHistory.Key))
                for (int slot = 0; slot < WallpaperHistory.Slots; slot++)
                {
                    if (slot < kept.Count) key.SetValue(WallpaperHistory.ValuePrefix + slot, kept[slot], RegistryValueKind.String);
                    else key.DeleteValue(WallpaperHistory.ValuePrefix + slot, throwOnMissingValue: false);
                }

            int deleted = 0, filesKept = 0;
            if (a.Bool(WallpaperHistory.DeleteFiles) == true)
            {
                // Only the student's own files: never the picture on the desktop now, Windows' own pictures or anything outside the profile.
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\') + "\\";
                foreach (var path in history.Where(path => remove.Contains(path, StringComparer.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var full = Path.GetFullPath(path);
                        if (!File.Exists(full)) continue;
                        if (IsCurrentWallpaper(path) || !full.StartsWith(profile, StringComparison.OrdinalIgnoreCase)) { filesKept++; continue; }
                        File.Delete(full);
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { filesKept++; }
                }
            }
            var data = new JsonObject
            {
                [WallpaperHistory.Removed] = history.Count - kept.Count,
                [WallpaperHistory.FilesDeleted] = deleted,
                [WallpaperHistory.FilesKept] = filesKept,
            };
            Log.Write($"wallpaper.recent.remove: {WallpaperHistory.Describe(data)}");
            return Response.Success(data);
        }
        catch (Exception ex) { return Response.Fail("Could not change the recent wallpaper images: " + ex.Message); }
    }

    public Response DeleteManagedWallpapers(JsonObject a)
    {
        try
        {
            Directory.CreateDirectory(ManagedDirectory);
            var names = a["names"]?.AsArray().Select(node => node?.GetValue<string>() ?? "").ToList() ?? [];
            if (names.Count == 0) return Response.Fail("Select at least one managed wallpaper to delete.");
            var currentPath = CurrentWallpaper();
            var current = currentPath.Length == 0 ? "" : Path.GetFullPath(currentPath);
            var paths = new List<string>();
            foreach (var name in names)
            {
                if (name.Length == 0 || Path.GetFileName(name) != name)
                    return Response.Fail("A managed wallpaper name is invalid.");
                var path = Path.GetFullPath(Path.Combine(ManagedDirectory, name));
                if (!path.StartsWith(Path.GetFullPath(ManagedDirectory) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    return Response.Fail("A managed wallpaper path is outside the managed folder.");
                if (path.Equals(current, StringComparison.OrdinalIgnoreCase))
                    return Response.Fail($"{name} is the current wallpaper. Choose another background before deleting it.");
                paths.Add(path);
            }
            var deleted = 0;
            foreach (var path in paths.Where(File.Exists)) { File.Delete(path); deleted++; }
            return Response.Success(new JsonObject { ["deleted"] = deleted });
        }
        catch (Exception ex) { return Response.Fail("Could not delete managed wallpapers: " + ex.Message); }
    }

    static string ManagedWallpaperPath(string name)
    {
        if (name.Length == 0 || Path.GetFileName(name) != name)
            throw new ArgumentException("A managed wallpaper name is invalid.");
        var root = Path.GetFullPath(ManagedDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(ManagedDirectory, name));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The managed wallpaper path is outside the managed folder.");
        return path;
    }

    public Response ResetWallpaper()
    {
        WallpaperState? state = null;
        try
        {
            if (File.Exists(StatePath))
                state = JsonSerializer.Deserialize<WallpaperState>(File.ReadAllText(StatePath));
        }
        catch { }

        // An empty saved path is meaningful: the user originally had a solid-color background.
        // Only a missing restore point or a missing non-empty image falls back to the stock image.
        var path = state?.OriginalPath ?? DefaultWallpaper();
        if (path.Length > 0 && !File.Exists(path)) path = DefaultWallpaper();

        if (!string.IsNullOrWhiteSpace(state?.OriginalColor))
        {
            using var colors = Registry.CurrentUser.CreateSubKey(@"Control Panel\Colors");
            colors.SetValue("Background", state.OriginalColor, RegistryValueKind.String);
        }
        var result = Apply(path, state?.OriginalStyle ?? "10", state?.OriginalTile ?? "0");
        if (result.Ok) { try { File.Delete(StatePath); } catch { } }
        return result;
    }

    static Response Apply(string path, string style, string tile)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(DesktopKey))
        {
            key.SetValue("WallpaperStyle", style, RegistryValueKind.String);
            key.SetValue("TileWallpaper", tile, RegistryValueKind.String);
        }
        if (!SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, Persist))
            return Response.Fail($"Windows refused the wallpaper change (error {Marshal.GetLastWin32Error()}).");
        return Response.Success();
    }

    static (string Style, string Tile) WallpaperStyle(string style) => style.ToLowerInvariant() switch
    {
        "fill" => ("10", "0"),
        "fit" => ("6", "0"),
        "stretch" => ("2", "0"),
        "tile" => ("0", "1"),
        "center" => ("0", "0"),
        "span" => ("22", "0"),
        _ => ("10", "0"),
    };

    static string CurrentWallpaper()
    {
        var sb = new StringBuilder(520);
        return SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) ? sb.ToString() : "";
    }

    static void SaveOriginalWallpaper()
    {
        if (File.Exists(StatePath)) return;
        using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
        using var colors = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
        var state = new WallpaperState
        {
            OriginalPath = CurrentWallpaper(),
            OriginalStyle = key?.GetValue("WallpaperStyle") as string,
            OriginalTile = key?.GetValue("TileWallpaper") as string,
            OriginalColor = colors?.GetValue("Background") as string,
        };
        Directory.CreateDirectory(ClientConfig.UserDir);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
    }

    static string StoreManagedWallpaper(string source, string suggestedName)
    {
        Directory.CreateDirectory(ManagedDirectory);
        using var stream = File.OpenRead(source);
        var hash = Convert.ToHexString(SHA256.HashData(stream))[..12];
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(suggestedName);
        stem = new string(stem.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').Take(60).ToArray()).Trim();
        if (stem.Length == 0) stem = "Wallpaper";
        var destination = Path.Combine(ManagedDirectory, $"{stem}-{hash}{extension}");
        if (!File.Exists(destination)) File.Copy(source, destination);
        return destination;
    }

    static string DefaultWallpaper()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        try
        {
            var defaultTheme = Path.Combine(windows, @"Resources\Themes\aero.theme");
            if (File.Exists(defaultTheme))
            {
                var setting = File.ReadLines(defaultTheme)
                    .FirstOrDefault(line => line.TrimStart().StartsWith("Wallpaper=", StringComparison.OrdinalIgnoreCase));
                var configured = setting?[(setting.IndexOf('=') + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    configured = Environment.ExpandEnvironmentVariables(configured);
                    if (File.Exists(configured)) return configured;
                }
            }
        }
        catch { }
        var primary = Path.Combine(windows, @"Web\Wallpaper\Windows\img0.jpg");
        if (File.Exists(primary)) return primary;
        try
        {
            return Directory.EnumerateFiles(Path.Combine(windows, @"Web\4K\Wallpaper\Windows"), "img0_*.jpg")
                .OrderByDescending(path => new FileInfo(path).Length)
                .FirstOrDefault() ?? "";
        }
        catch { return ""; }
    }

    // ---- Mouse ----------------------------------------------------------------

    public Response SetMouse(JsonObject a)
    {
        var errors = new List<string>();

        if (a.Int("speed") is int speed)
        {
            speed = Math.Clamp(speed, 1, 20);
            if (!SystemParametersInfo(SPI_SETMOUSESPEED, 0, (IntPtr)speed, Persist)) errors.Add("speed");
        }
        if (a.Int("doubleClickMs") is int dbl)
        {
            dbl = Math.Clamp(dbl, 200, 900);
            if (!SystemParametersInfo(SPI_SETDOUBLECLICKTIME, (uint)dbl, IntPtr.Zero, Persist)) errors.Add("double-click");
        }
        if (a.Bool("swapButtons") is bool swap)
        {
            if (!SystemParametersInfo(SPI_SETMOUSEBUTTONSWAP, swap ? 1u : 0u, IntPtr.Zero, Persist)) errors.Add("button swap");
        }
        if (a.Int("wheelLines") is int lines)
        {
            lines = Math.Clamp(lines, 1, 100);
            if (!SystemParametersInfo(SPI_SETWHEELSCROLLLINES, (uint)lines, IntPtr.Zero, Persist)) errors.Add("wheel");
        }
        if (a.Bool("precision") is bool precision)
        {
            // "Enhance pointer precision": {threshold1, threshold2, acceleration}
            var accel = precision ? new[] { 6, 10, 1 } : new[] { 0, 0, 0 };
            if (!SystemParametersInfo(SPI_SETMOUSE, 0, accel, Persist)) errors.Add("pointer precision");
        }

        return errors.Count == 0
            ? Response.Success()
            : Response.Fail("Could not change: " + string.Join(", ", errors));
    }

    /// <summary>Windows factory defaults.</summary>
    public Response ResetMouse() => SetMouse(new JsonObject
    {
        ["speed"] = 10,
        ["doubleClickMs"] = 500,
        ["swapButtons"] = false,
        ["wheelLines"] = 3,
        ["precision"] = true,
    });
}
