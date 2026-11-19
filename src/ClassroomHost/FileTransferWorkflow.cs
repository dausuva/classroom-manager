// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>What the file workflows need from one connected computer: the two transfers and the calls. <see cref="ClientSession"/> is the real one.</summary>
public interface IFilePeer
{
    Task UploadAsync(string localPath, string remotePath, IProgress<double>? progress);
    Task DownloadAsync(string remotePath, string localPath, IProgress<double>? progress);
    Task<JsonObject> CallAtAsync(string level, string cmd, JsonObject? args);
    Task<JsonObject> CallAsync(string cmd, JsonObject? args);
}

/// <summary>The non-owning adapter that lets the workflows run on a <see cref="ClientSession"/>.</summary>
public sealed class ClientSessionPeer(ClientSession session) : IFilePeer
{
    public Task UploadAsync(string localPath, string remotePath, IProgress<double>? progress) => session.UploadAsync(localPath, remotePath, progress);
    public Task DownloadAsync(string remotePath, string localPath, IProgress<double>? progress) => session.DownloadAsync(remotePath, localPath, progress);
    public Task<JsonObject> CallAtAsync(string level, string cmd, JsonObject? args) => session.CallAtAsync(level, cmd, args);
    public Task<JsonObject> CallAsync(string cmd, JsonObject? args) => session.CallAsync(cmd, args);
}

/// <summary>Where a file sent to the students goes: the folder the teacher chose and an optional relative subfolder inside it (MW-F).</summary>
public static class FileDestination
{
    /// <summary>The destination folder, or null with an English <paramref name="problem"/> the teacher can act on.</summary>
    public static string? Resolve(string folder, string subfolder, out string problem)
    {
        problem = "";
        if (folder.Length == 0) { problem = "Choose or enter the folder to put the files in."; return null; }
        if (subfolder.Length == 0) return folder;
        if (Path.IsPathRooted(subfolder) || subfolder.Split('\\', '/').Any(part => part == ".."))
        {
            problem = "Enter a relative subfolder without drive letters or '..'.";
            return null;
        }
        return Path.Combine(folder, subfolder);
    }
}

/// <summary>
/// Sending files to a student computer and collecting them back (MW-F), without a window. The steps and the rules are the ones the Host always had:
/// a file is staged, delivered into the student's own folder, and the staged copy is removed even when delivery fails; a file that cannot be read or
/// written is named in the result instead of stopping the others; a half-written copy is removed; a broken connection still ends the run for that computer.
/// </summary>
public static class FileTransferWorkflow
{
    /// <summary>Sends every file; returns how many were delivered. Throws on the first failure, as the run for that computer then ends.</summary>
    public static async Task<int> SendAsync(IFilePeer peer, ClientInfo computer, IReadOnlyList<string> files, string stagingFolder,
        string destination, bool overwrite, Action<FileTransferRecord> record, Action<string> status)
    {
        int done = 0, number = 0;
        foreach (var file in files)
        {
            var position = ++number;
            var name = Path.GetFileName(file);
            var staged = $@"{stagingFolder}\send\{name}";
            try
            {
                var progress = new Progress<double>(p => status($"Sending {position} of {files.Count}: {name} {p:P0}"));
                await peer.UploadAsync(file, staged, progress);
                var delivered = await peer.CallAtAsync(Levels.User, Cmd.FsDeliver, new JsonObject
                {
                    ["source"] = staged,
                    ["name"] = name,
                    ["destination"] = destination,
                    ["overwrite"] = overwrite,
                });
                var sha256 = delivered["sha256"]?.GetValue<string>() ?? "";
                record(new FileTransferRecord
                {
                    Computer = computer.Name,
                    Address = computer.Address,
                    ClientId = computer.ClientId,
                    FileName = name,
                    DestinationPath = delivered["path"]?.GetValue<string>() ?? Path.Combine(destination, name),
                    Size = delivered["size"]?.GetValue<long>() ?? new FileInfo(file).Length,
                    Sha256 = sha256,
                    Status = sha256.Length == 64 ? FileTransferStatuses.Delivered : FileTransferStatuses.UndoUnavailable,
                });
                done++;
            }
            finally
            {
                try { await peer.CallAsync(Cmd.FsDelete, new JsonObject { ["path"] = staged }); } catch { }
            }
        }
        status($"Sent {done} file(s)");
        return done;
    }

    public sealed record CollectResult(int Copied, IReadOnlyList<string> Failed, int Kept, string StudentFolder);

    /// <summary>Finds the matching files in a folder on the computer and copies them into one subfolder of <paramref name="localRoot"/> for that student.</summary>
    public static async Task<CollectResult> CollectAsync(IFilePeer peer, ClientInfo computer, string localRoot, string folder, string pattern,
        bool recursive, bool overwrite, Action<string> status)
    {
        var found = await peer.CallAtAsync(Levels.User, Cmd.FsFind, new JsonObject
        {
            ["folder"] = folder,
            ["pattern"] = pattern,
            ["recursive"] = recursive,
        });
        var remoteFolder = found["folder"]!.GetValue<string>();
        var list = found["files"]!.AsArray();

        var studentDir = Path.Combine(localRoot, Sanitize(string.IsNullOrEmpty(computer.User) ? computer.Name : $"{computer.Name}_{computer.User}"));
        var files = list.Select(f => f!["path"]!.GetValue<string>())
            .Select(remote => (Remote: remote, Relative: Path.GetRelativePath(remoteFolder, remote)))
            .Where(file => FolderSync.SafeRelativePath(file.Relative))
            .Select(file => (file.Remote, Local: Path.Combine(studentDir, file.Relative), Modified: DateTime.MinValue))
            .ToList();
        var kept = overwrite ? 0 : files.RemoveAll(file => File.Exists(file.Local));
        var (copied, failed) = await DownloadFilesAsync(peer, files, status);
        status(failed.Count == 0 ? $"Collected {copied} file(s)" : $"Collected {copied} file(s), {failed.Count} could not be copied");
        return new CollectResult(copied, failed, kept, studentDir);
    }

    /// <summary>
    /// Copies files from a student computer one by one. A file that cannot be read there or written here is skipped and named in the result, so one
    /// file in use does not stop the rest; a half-written copy is removed. A broken connection still ends the run for that computer.
    /// </summary>
    public static async Task<(int Copied, List<string> Failed)> DownloadFilesAsync(IFilePeer peer,
        IEnumerable<(string Remote, string Local, DateTime Modified)> files, Action<string> status)
    {
        int copied = 0;
        var failed = new List<string>();
        var all = files.ToList();
        int number = 0;
        foreach (var (remote, local, modified) in all)
        {
            var position = ++number;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                var progress = new Progress<double>(p => status($"Copying {position} of {all.Count}: {Path.GetFileName(remote)} {p:P0}"));
                await peer.DownloadAsync(remote, local, progress);
                // A synced copy carries the student's change time, so the next sync sees it as unchanged.
                if (modified != DateTime.MinValue) File.SetLastWriteTime(local, modified);
                copied++;
            }
            catch (Exception ex) when (ex is RemoteException or UnauthorizedAccessException ||
                                       ex is IOException and not EndOfStreamException && ex.InnerException is not SocketException)
            {
                failed.Add($"{remote}: {ex.Message}");
                try { File.Delete(local); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        }
        return (copied, failed);
    }

    /// <summary>A computer or user name as a folder name.</summary>
    public static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(ch => bad.Contains(ch) ? '_' : ch));
    }
}
