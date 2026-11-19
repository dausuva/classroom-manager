// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>MW-F: sending files to a student computer and collecting them back, on a fake computer; the real transfer is tested with the session itself.</summary>
public sealed class FileTransferWorkflowTests : IDisposable
{
    readonly string _folder = Path.Combine(TestDataFolder.Scratch, "file-workflow-" + Guid.NewGuid().ToString("N"));

    public FileTransferWorkflowTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    /// <summary>A student computer that records what it is asked and can be told to fail.</summary>
    sealed class FakePeer : IFilePeer
    {
        public readonly List<string> Calls = [];
        public readonly List<string> Uploaded = [];
        public Func<string, Exception?> DownloadFails = _ => null;
        public JsonObject Delivered = new() { ["path"] = @"C:\Users\s\Desktop\a.txt", ["size"] = 3L, ["sha256"] = new string('a', 64) };
        public JsonObject Found = new();
        public bool DeliverFails;

        public Task UploadAsync(string localPath, string remotePath, IProgress<double>? progress) { Uploaded.Add(remotePath); return Task.CompletedTask; }

        public Task DownloadAsync(string remotePath, string localPath, IProgress<double>? progress)
        {
            if (DownloadFails(remotePath) is { } error) { File.WriteAllText(localPath, "half"); return Task.FromException(error); }
            File.WriteAllText(localPath, "content");
            return Task.CompletedTask;
        }

        public Task<JsonObject> CallAtAsync(string level, string cmd, JsonObject? args)
        {
            Calls.Add(cmd);
            if (cmd == Cmd.FsDeliver && DeliverFails) return Task.FromException<JsonObject>(new InvalidOperationException("refused"));
            return Task.FromResult(cmd == Cmd.FsDeliver ? Delivered : Found);
        }

        public Task<JsonObject> CallAsync(string cmd, JsonObject? args) { Calls.Add(cmd + ":" + args?["path"]); return Task.FromResult(new JsonObject()); }
    }

    static ClientInfo Computer() => new() { Name = "PC-1", Address = "10.0.0.5", ClientId = "id-1", User = "ana" };

    string Local(string name, string text = "abc")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Theory]
    [InlineData("%DESKTOP%", "", "%DESKTOP%")]
    [InlineData(@"C:\Work", "week1", @"C:\Work\week1")]
    public void The_destination_is_the_folder_with_the_optional_subfolder_inside_it(string folder, string sub, string expected) =>
        Assert.Equal(expected, FileDestination.Resolve(folder, sub, out _));

    [Theory]
    [InlineData("", "", "Choose or enter the folder to put the files in.")]
    [InlineData("%DESKTOP%", @"C:\other", "Enter a relative subfolder without drive letters or '..'.")]
    [InlineData("%DESKTOP%", @"..\up", "Enter a relative subfolder without drive letters or '..'.")]
    [InlineData("%DESKTOP%", "a/../b", "Enter a relative subfolder without drive letters or '..'.")]
    public void A_destination_that_is_empty_or_leaves_the_folder_is_refused_with_a_reason(string folder, string sub, string reason)
    {
        Assert.Null(FileDestination.Resolve(folder, sub, out var problem));
        Assert.Equal(reason, problem);
    }

    [Fact]
    public async Task Every_file_is_staged_delivered_recorded_and_the_staged_copy_removed()
    {
        var peer = new FakePeer();
        var records = new List<FileTransferRecord>();
        var statuses = new List<string>();

        var done = await FileTransferWorkflow.SendAsync(peer, Computer(), [Local("a.txt"), Local("b.txt")], @"C:\Staging\host", @"C:\Dest", true, records.Add, statuses.Add);

        Assert.Equal(2, done);
        Assert.Equal([@"C:\Staging\host\send\a.txt", @"C:\Staging\host\send\b.txt"], peer.Uploaded);
        Assert.Equal(2, records.Count);
        Assert.All(records, record =>
        {
            Assert.Equal("PC-1", record.Computer);
            Assert.Equal("id-1", record.ClientId);
            Assert.Equal(FileTransferStatuses.Delivered, record.Status);
        });
        Assert.Equal(2, peer.Calls.Count(call => call.StartsWith(Cmd.FsDelete + ":")));
        Assert.Contains("Sent 2 file(s)", statuses);
    }

    [Fact]
    public async Task A_delivery_without_a_checksum_cannot_be_undone_and_says_so()
    {
        var peer = new FakePeer { Delivered = new JsonObject { ["path"] = @"C:\x\a.txt" } };
        var records = new List<FileTransferRecord>();

        await FileTransferWorkflow.SendAsync(peer, Computer(), [Local("a.txt", "four")], @"C:\S", @"C:\x", false, records.Add, _ => { });

        var record = Assert.Single(records);
        Assert.Equal(FileTransferStatuses.UndoUnavailable, record.Status);
        Assert.Equal(4, record.Size);
    }

    [Fact]
    public async Task A_failed_delivery_still_removes_the_staged_copy_and_ends_the_run_for_that_computer()
    {
        var peer = new FakePeer { DeliverFails = true };
        var records = new List<FileTransferRecord>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FileTransferWorkflow.SendAsync(peer, Computer(), [Local("a.txt"), Local("b.txt")], @"C:\S", @"C:\x", true, records.Add, _ => { }));

        Assert.Empty(records);
        Assert.Single(peer.Uploaded);
        Assert.Contains(peer.Calls, call => call == Cmd.FsDelete + @":C:\S\send\a.txt");
    }

    static JsonObject Listing(string folder, params string[] files) => new()
    {
        ["folder"] = folder,
        ["files"] = new JsonArray(files.Select(file => (JsonNode)new JsonObject { ["path"] = file }).ToArray()),
    };

    [Fact]
    public async Task Collecting_copies_the_found_files_into_a_folder_named_for_the_computer_and_user_and_skips_unsafe_paths()
    {
        var peer = new FakePeer { Found = Listing(@"C:\Docs", @"C:\Docs\one.docx", @"C:\Docs\sub\two.docx", @"D:\elsewhere\evil.docx") };
        var statuses = new List<string>();

        var result = await FileTransferWorkflow.CollectAsync(peer, Computer(), _folder, @"C:\Docs", "*.docx", true, true, statuses.Add);

        Assert.Equal(2, result.Copied);
        Assert.Empty(result.Failed);
        Assert.Equal(Path.Combine(_folder, "PC-1_ana"), result.StudentFolder);
        Assert.True(File.Exists(Path.Combine(result.StudentFolder, "one.docx")));
        Assert.True(File.Exists(Path.Combine(result.StudentFolder, "sub", "two.docx")));
        Assert.Contains("Collected 2 file(s)", statuses);
    }

    [Fact]
    public async Task Without_overwrite_files_already_there_are_kept_and_counted()
    {
        var peer = new FakePeer { Found = Listing(@"C:\Docs", @"C:\Docs\one.docx", @"C:\Docs\two.docx") };
        var existing = Path.Combine(_folder, "PC-1_ana");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "one.docx"), "mine");

        var result = await FileTransferWorkflow.CollectAsync(peer, Computer(), _folder, @"C:\Docs", "*", false, false, _ => { });

        Assert.Equal(1, result.Copied);
        Assert.Equal(1, result.Kept);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(existing, "one.docx")));
    }

    [Fact]
    public async Task One_file_that_cannot_be_read_is_named_and_its_partial_copy_removed_while_the_rest_are_copied()
    {
        var peer = new FakePeer { Found = Listing(@"C:\Docs", @"C:\Docs\busy.docx", @"C:\Docs\fine.docx") };
        peer.DownloadFails = remote => remote.EndsWith("busy.docx") ? new IOException("in use") : null;

        var result = await FileTransferWorkflow.CollectAsync(peer, Computer(), _folder, @"C:\Docs", "*", false, true, _ => { });

        Assert.Equal(1, result.Copied);
        Assert.Contains(@"C:\Docs\busy.docx: in use", result.Failed.Single());
        Assert.False(File.Exists(Path.Combine(result.StudentFolder, "busy.docx")));
        Assert.True(File.Exists(Path.Combine(result.StudentFolder, "fine.docx")));
    }

    [Fact]
    public async Task A_broken_connection_is_not_a_skipped_file_it_ends_the_run()
    {
        var peer = new FakePeer { Found = Listing(@"C:\Docs", @"C:\Docs\one.docx") };
        peer.DownloadFails = _ => new IOException("reset", new SocketException());

        await Assert.ThrowsAsync<IOException>(() => FileTransferWorkflow.CollectAsync(peer, Computer(), _folder, @"C:\Docs", "*", false, true, _ => { }));
    }

    [Fact]
    public async Task A_synced_copy_gets_the_change_time_of_the_student_s_file()
    {
        var peer = new FakePeer();
        var stamp = new DateTime(2026, 10, 14, 9, 15, 2);
        var local = Path.Combine(_folder, "synced", "a.txt");

        var (copied, failed) = await FileTransferWorkflow.DownloadFilesAsync(peer, [(@"C:\a.txt", local, stamp)], _ => { });

        Assert.Equal(1, copied);
        Assert.Empty(failed);
        Assert.Equal(stamp, File.GetLastWriteTime(local));
    }

    [Fact]
    public void A_name_with_characters_a_folder_cannot_have_is_made_safe()
    {
        Assert.Equal("PC_1_ana_x", FileTransferWorkflow.Sanitize("PC/1:ana?x".Replace(":", "_").Replace("?", "_").Replace("/", "_") ));
        Assert.DoesNotContain(FileTransferWorkflow.Sanitize("a<b>c|d\"e"), Path.GetInvalidFileNameChars().Select(ch => ch.ToString()).ToArray());
    }
}
