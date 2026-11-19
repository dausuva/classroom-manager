// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;

namespace ClassroomManager.Shared;

/// <summary>
/// The "Recent images" row of Windows' Background settings (wallpaper.recent.list / wallpaper.recent.remove).
/// Windows keeps it for each user as up to five registry values, BackgroundHistoryPath0 (newest) to
/// BackgroundHistoryPath4, under <see cref="Key"/>. Removing an image closes the gap, so the rest keep their order.
/// </summary>
public static class WallpaperHistory
{
    public const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers";
    public const string ValuePrefix = "BackgroundHistoryPath";
    public const int Slots = 5;

    public const string Images = "images";
    public const string Paths = "paths";
    public const string DeleteFiles = "deleteFiles";
    public const string Removed = "removed";
    public const string FilesDeleted = "filesDeleted";
    public const string FilesKept = "filesKept";

    /// <summary>One recent image as the client reports it; the preview is a small JPEG in base64, "" when unreadable.</summary>
    public sealed record Image(string Path, bool Exists, long Size, bool Current, string Preview)
    {
        public JsonObject ToJson() => new()
        {
            ["path"] = Path, ["exists"] = Exists, ["size"] = Size, ["current"] = Current, ["preview"] = Preview,
        };

        public static Image FromJson(JsonNode? node) => new(
            node?["path"]?.GetValue<string>() ?? "", node?["exists"]?.GetValue<bool>() == true,
            node?["size"] is JsonNode size && long.TryParse(size.ToJsonString(), out var bytes) ? bytes : 0,
            node?["current"]?.GetValue<bool>() == true, node?["preview"]?.GetValue<string>() ?? "");
    }

    public static List<Image> ImagesFrom(JsonObject data) => (data[Images] as JsonArray ?? []).Select(Image.FromJson).ToList();

    /// <summary>The history without the given paths (compared ignoring case), in the same order; empty slots dropped.</summary>
    public static List<string> Without(IEnumerable<string> history, IEnumerable<string> remove)
    {
        var gone = remove.Select(path => path.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return history.Where(path => !string.IsNullOrWhiteSpace(path) && !gone.Contains(path.Trim())).ToList();
    }

    public static string Describe(JsonObject data)
    {
        static long Number(JsonNode? node) => node is not null && long.TryParse(node.ToJsonString(), out var value) ? value : 0;
        var parts = new List<string> { $"{Number(data[Removed])} image(s) removed from Recent images." };
        if (Number(data[FilesDeleted]) > 0) parts.Add($"{Number(data[FilesDeleted])} file(s) deleted.");
        if (Number(data[FilesKept]) > 0) parts.Add($"{Number(data[FilesKept])} file(s) kept: on the desktop now, outside the user's folder or not deletable.");
        return string.Join(" ", parts);
    }
}
