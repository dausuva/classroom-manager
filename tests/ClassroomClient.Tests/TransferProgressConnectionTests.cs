// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

public sealed class TransferProgressConnectionTests
{
    const string Key = "synthetic-transfer-progress-key";

    [Theory]
    [InlineData(0, "")]
    [InlineData(17, "")]
    [InlineData(Protocol.ChunkSize * 2 + 17, "")]
    [InlineData(Protocol.ChunkSize * 2 + 17, "upload")]
    [InlineData(Protocol.ChunkSize * 2 + 17, "download")]
    public async Task Authenticated_upload_and_download_report_acknowledged_progress_and_preserve_bytes(int size, string failure)
    {
        var root = Directory.CreateTempSubdirectory("transfer-progress-").FullName;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var bytes = Enumerable.Range(0, size).Select(index => (byte)(index % 251)).ToArray();
        using var uploaded = new MemoryStream();
        var server = new CommandServer(new ClientConfig { UseTls = false }, new KeyStore(Key, []), async (request, _, _, ct) =>
        {
            await Task.Delay(5, ct);
            var offset = request.Args!["offset"]!.GetValue<long>();
            if (offset > 0 && (failure == "upload" && request.Cmd == Cmd.FsWrite || failure == "download" && request.Cmd == Cmd.FsRead))
                return Response.Fail("synthetic transfer refusal");
            if (request.Cmd == Cmd.FsWrite)
            {
                var payload = Convert.FromBase64String(request.Args["data"]!.GetValue<string>());
                Assert.Equal(uploaded.Length, offset);
                uploaded.Write(payload);
                return Response.Success();
            }
            var count = Math.Min(Protocol.ChunkSize, size - (int)offset);
            return Response.Success(new JsonObject
            { ["data"] = Convert.ToBase64String(bytes, (int)offset, count), ["total"] = size, ["eof"] = offset + count >= size });
        });
        async Task ServeAsync()
        {
            var tcp = await listener.AcceptTcpClientAsync(stop.Token);
            await server.HandleAsync(tcp, stop.Token);
        }
        var serving = ServeAsync();
        try
        {
            var source = Path.Combine(root, "source.bin");
            var copy = Path.Combine(root, "copy.bin");
            File.WriteAllBytes(source, bytes);
            using var session = await ClientSession.OpenAsync(new ClientInfo { Address = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port },
                new HostSettings { UseTls = false, Psk = Key }, stop.Token);
            var updates = new List<FileTransferProgress>();
            session.TransferProgress += updates.Add;
            if (failure == "upload")
            {
                await Assert.ThrowsAsync<RemoteException>(() => session.UploadAsync(source, "synthetic.bin", ct: stop.Token));
                Assert.DoesNotContain(updates, update => update.Complete || update.Percent == 100);
                return;
            }
            await session.UploadAsync(source, "synthetic.bin", ct: stop.Token);
            Assert.Equal(bytes, uploaded.ToArray());
            Verify(updates, FileTransferDirection.Upload, size);
            updates.Clear();
            if (failure == "download")
            {
                await Assert.ThrowsAsync<RemoteException>(() => session.DownloadAsync("synthetic.bin", copy, ct: stop.Token));
                Assert.DoesNotContain(updates, update => update.Complete || update.Percent == 100);
                return;
            }
            await session.DownloadAsync("synthetic.bin", copy, ct: stop.Token);
            Assert.Equal(bytes, File.ReadAllBytes(copy));
            Verify(updates, FileTransferDirection.Download, size);
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            await serving;
            Directory.Delete(root, true);
        }
    }

    static void Verify(List<FileTransferProgress> updates, FileTransferDirection direction, int size)
    {
        Assert.NotEmpty(updates);
        Assert.All(updates, update => Assert.Equal(direction, update.Direction));
        Assert.Equal(100, updates[^1].Percent);
        Assert.Equal(size, updates[^1].Bytes);
        Assert.Equal(size, updates[^1].TotalBytes);
        Assert.True(updates[^1].Complete);
        Assert.True(updates[^1].MegabitsPerSecond >= 0);
        if (size > 0) Assert.True(updates[^1].MegabitsPerSecond > 0);
        Assert.Equal(updates.Select(update => update.Bytes).Order(), updates.Select(update => update.Bytes));
    }
}
