// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Modules;

/// <summary>
/// A file the teacher picked in a window of the Host. The module holds this handle, never the path: the Host keeps the path and the module can
/// only hand the handle back to <see cref="IModuleUploads"/>. The Host fills every field itself, so a handle a module invents names nothing.
/// </summary>
/// <param name="Id">Meaningful only inside the run that issued it.</param>
/// <param name="Name">The file name, for messages and for the kind check on its extension.</param>
/// <param name="Size">The size in bytes when the file was picked.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the content when the file was picked. The Host checks it again before the file leaves this PC.</param>
public sealed record ModulePickedFile(string Id, string Name, long Size, string Sha256);

/// <summary>Where the file now is on the computer. The path is in the Host's own staging folder there, which is where the Client reads uploaded files from.</summary>
public sealed record ModuleUploadResult(string RemotePath);

/// <summary>The kinds of upload a module can declare (<see cref="ModuleDescriptor.Uploads"/>) and the rules for each.</summary>
public static class ModuleUploads
{
    /// <summary>A picture for the desktop, the lock screen or the sign-in screen. It is stored on the computer as <c>wallpaper.png</c> (or the file's own image extension).</summary>
    public const string Wallpaper = "wallpaper";

    public static readonly IReadOnlyList<string> Known = [Wallpaper];

    /// <summary>The image types the Host's picker, the lock screen and the sign-in screen all accept. The Client still checks the content.</summary>
    public static readonly IReadOnlyList<string> ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp"];

    public const long MaxImageBytes = 32L * 1024 * 1024;

    /// <summary>Why a file cannot be uploaded as <paramref name="drop"/>, in plain words, or null.</summary>
    public static string? Problem(string drop, string fileName, long size)
    {
        if (!Known.Contains(drop, StringComparer.Ordinal)) return $"\"{drop}\" is not a kind of upload this Host offers.";
        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (!ImageExtensions.Contains(extension)) return "The picture must be a JPG, PNG or BMP file.";
        if (size < 1) return "The picture is empty.";
        if (size > MaxImageBytes) return "The picture is larger than 32 MB.";
        return null;
    }

    /// <summary>The name the file gets in the staging folder: fixed by the kind, so a module can never choose where in the folder a file goes.</summary>
    public static string StagedName(string drop, string fileName) => drop + Path.GetExtension(fileName).ToLowerInvariant();
}

/// <summary>
/// Port <c>client.upload</c>: sends a file the teacher picked to one chosen computer's staging folder. It needs <c>storage.source</c> too, because
/// the file is named by a handle the Host issued, and the kind of upload must be one the module declared. The module gets back where the file is
/// so it can name it in a command it declared; the Client checks the file again before it uses it.
/// </summary>
public interface IModuleUploads
{
    Task<ModuleUploadResult> UploadAsync(ModuleComputer computer, ModulePickedFile source, string drop, CancellationToken cancellation = default);
}
