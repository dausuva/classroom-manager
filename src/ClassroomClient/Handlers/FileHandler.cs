// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>
/// File management with the client's (administrator) rights, restricted to
/// the configured roots plus the staging folder used for uploads.
/// </summary>
sealed class FileHandler
{
    readonly ContainedFiles _files;

    public FileHandler(ClientConfig config)
    {
        _files = new(config.AllowedRoots.Append(ClientConfig.StagingDir));
    }

    public Response Roots()
    {
        var arr = new JsonArray();
        foreach (var r in _files.Roots) arr.Add(r);
        return Response.Success(new JsonObject { ["roots"] = arr });
    }

    public Response List(JsonObject a)
    {
        using var dir = _files.Directory(a.Str("path"));
        var entries = new JsonArray();
        foreach (var e in ContainedFiles.Entries(dir.Handle))
        {
            entries.Add(new JsonObject
            {
                ["name"] = e.Name,
                ["isDir"] = e.IsDirectory,
                ["size"] = e.IsDirectory ? 0 : e.Size,
                ["modified"] = e.Modified.ToString("yyyy-MM-dd HH:mm"),
            });
        }
        return Response.Success(new JsonObject { ["path"] = dir.Path, ["entries"] = entries });
    }

    public Response Mkdir(JsonObject a)
    {
        using var directory = _files.Directory(a.Str("path"), create: true);
        return Response.Success();
    }

    public Response Delete(JsonObject a)
    {
        _files.Remove(a.Str("path"));
        return Response.Success();
    }

    public Response Rename(JsonObject a)
    {
        _files.Rename(a.Str("path"), a.Str("newPath"));
        return Response.Success();
    }

    public async Task<Response> ReadAsync(JsonObject a, CancellationToken ct)
    {
        try { return await ReadCoreAsync(a, ct); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return Response.Fail("File not found."); }
    }

    async Task<Response> ReadCoreAsync(JsonObject a, CancellationToken ct)
    {
        long offset = a.Long("offset");
        int count = Math.Min(a.Int("count") ?? Protocol.ChunkSize, Protocol.ChunkSize);
        if (offset < 0 || count < 0) throw new ArgumentOutOfRangeException(nameof(a));
        using var lease = _files.File(a.Str("path"), write: false, create: false);
        await using var fs = new FileStream(lease.Handle, FileAccess.Read, 4096, isAsync: false);
        long total = fs.Length;
        fs.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await fs.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) break;
            read += n;
        }
        return Response.Success(new JsonObject
        {
            ["data"] = Convert.ToBase64String(buf, 0, read),
            ["total"] = total,
            ["eof"] = offset + read >= total,
        });
    }

    public async Task<Response> WriteAsync(JsonObject a, CancellationToken ct)
    {
        long offset = a.Long("offset");
        var bytes = Convert.FromBase64String(a.Str("data"));
        if (offset < 0 || bytes.Length > Protocol.ChunkSize) throw new ArgumentOutOfRangeException(nameof(a));
        // offset 0 starts a fresh file, later chunks append at their offset
        using var lease = _files.File(a.Str("path"), write: true, create: offset == 0);
        await using var fs = new FileStream(lease.Handle, FileAccess.Write, 4096, isAsync: false);
        if (offset == 0) fs.SetLength(0); // Validate the opened object before truncation.
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(bytes, ct);
        MemoryReclaimer.NoteTransferActivity(
            a.Long("total", offset + bytes.Length),
            a.Bool("complete") == true);
        return Response.Success(new JsonObject { ["written"] = bytes.Length });
    }
}
