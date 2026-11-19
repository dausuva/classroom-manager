// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using ClassroomManager.Client;
using Xunit;

namespace ClassroomClient.Tests;

public sealed class FileContainmentTests : IDisposable
{
    readonly string _scratch = Directory.CreateTempSubdirectory("cm containment ").FullName;
    string Allowed => Path.Combine(_scratch, "Allowed");
    string Outside => Path.Combine(_scratch, "Outside");
    FileHandler Handler => new(new ClientConfig { AllowedRoots = [Allowed] });
    public FileContainmentTests() { Directory.CreateDirectory(Allowed); Directory.CreateDirectory(Outside); }
    public void Dispose() => Directory.Delete(_scratch, true);
    static JsonObject Args(string path) => new() { ["path"] = path };
    static JsonObject WriteArgs(string path, string text, int offset = 0) => new()
        { ["path"] = path, ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), ["offset"] = (long)offset };

    [Fact]
    public async Task Normal_chunk_write_read_list_rename_and_recursive_delete_use_the_opened_objects()
    {
        var path = Path.Combine(Allowed, "new", "nested", "file.txt");
        var handler = Handler;
        Assert.True((await handler.WriteAsync(WriteArgs(path, "long original"), default)).Ok);
        Assert.True((await handler.WriteAsync(WriteArgs(path, "ab"), default)).Ok);
        Assert.True((await handler.WriteAsync(WriteArgs(path, "cd", 2), default)).Ok);
        var response = await handler.ReadAsync(Args(path), default);
        Assert.Equal("abcd", Encoding.UTF8.GetString(Convert.FromBase64String(response.Data["data"]!.GetValue<string>())));
        var folder = Path.GetDirectoryName(path)!;
        var list = handler.List(Args(folder));
        Assert.Equal("file.txt", Assert.Single(list.Data["entries"]!.AsArray())!["name"]!.GetValue<string>());
        var renamed = Path.Combine(folder, "renamed.txt");
        Assert.True(handler.Rename(new JsonObject { ["path"] = path, ["newPath"] = renamed }).Ok);
        Assert.Equal("abcd", File.ReadAllText(renamed));
        var movedFolder = Path.Combine(Allowed, "moved");
        Assert.True(handler.Rename(new JsonObject { ["path"] = Path.Combine(Allowed, "new"), ["newPath"] = movedFolder }).Ok);
        Assert.True(handler.Delete(Args(movedFolder)).Ok);
        Assert.False(Directory.Exists(movedFolder));
        Assert.True(handler.Mkdir(Args(Path.Combine(Allowed, "empty", "nested"))).Ok);
    }

    [Fact]
    public async Task Junction_ancestors_and_root_junctions_refuse_every_file_operation_without_touching_the_target()
    {
        var target = Path.Combine(Outside, "secret.txt");
        File.WriteAllText(target, "outside marker");
        var link = Path.Combine(Allowed, "Link");
        Junction(link, Outside);
        try
        {
            var path = Path.Combine(link, "secret.txt");
            var handler = Handler;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => handler.ReadAsync(Args(path), default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => handler.WriteAsync(WriteArgs(path, "changed"), default));
            Assert.Throws<UnauthorizedAccessException>(() => handler.List(Args(link)));
            Assert.Throws<UnauthorizedAccessException>(() => handler.Mkdir(Args(Path.Combine(link, "new"))));
            Assert.Throws<UnauthorizedAccessException>(() => handler.Delete(Args(link)));
            Assert.Throws<UnauthorizedAccessException>(() => handler.Delete(Args(path)));
            Assert.Throws<UnauthorizedAccessException>(() => handler.Rename(new JsonObject { ["path"] = path, ["newPath"] = Path.Combine(Allowed, "stolen.txt") }));
            Assert.Throws<UnauthorizedAccessException>(() => handler.Delete(Args(Path.GetDirectoryName(link)! + "\\.")));
            var rootHandler = new FileHandler(new ClientConfig { AllowedRoots = [link] });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rootHandler.ReadAsync(Args(path), default));
            Assert.Equal("outside marker", File.ReadAllText(target));
            Assert.False(Directory.Exists(Path.Combine(Outside, "new")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Hard_link_is_rejected_before_truncation_read_delete_or_rename()
    {
        var outside = Path.Combine(Outside, "original.txt");
        File.WriteAllText(outside, "outside hard-link marker");
        var link = Path.Combine(Allowed, "linked.txt");
        Assert.True(CreateHardLinkW(link, outside, IntPtr.Zero), new Win32Exception(Marshal.GetLastWin32Error()).Message);
        var handler = Handler;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => handler.WriteAsync(WriteArgs(link, "changed"), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => handler.ReadAsync(Args(link), default));
        Assert.Throws<UnauthorizedAccessException>(() => handler.Delete(Args(link)));
        Assert.Throws<UnauthorizedAccessException>(() => handler.Rename(new JsonObject { ["path"] = link, ["newPath"] = Path.Combine(Allowed, "renamed.txt") }));
        Assert.Equal("outside hard-link marker", File.ReadAllText(outside));
    }

    [Fact]
    public void Held_parent_cannot_be_replaced_and_all_ancestors_are_released_on_disposal()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Allowed, "parent")).FullName;
        var path = Path.Combine(folder, "file.txt");
        File.WriteAllText(path, "inside marker");
        var files = new ContainedFiles([Allowed]);
        for (var i = 0; i < 50; i++)
        {
            using var lease = files.File(path, write: false, create: false);
            Assert.Throws<IOException>(() => Directory.Move(folder, folder + "-swapped"));
            using var stream = new FileStream(lease.Handle, FileAccess.Read);
            Assert.Equal((int)'i', stream.ReadByte());
        }
        Directory.Move(folder, folder + "-released");
        Assert.True(File.Exists(Path.Combine(folder + "-released", "file.txt")));
    }

    [Theory]
    [InlineData("..\\Outside\\secret.txt")]
    [InlineData("file.txt:secret")]
    [InlineData("NUL")]
    [InlineData("folder.\\file.txt")]
    [InlineData("folder \\file.txt")]
    public async Task Traversal_streams_and_ambiguous_names_are_refused(string relative)
    {
        var handler = Handler;
        var path = Path.Combine(Allowed, relative);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => handler.WriteAsync(WriteArgs(path, "changed"), default));
    }

    [Fact]
    public void Root_rename_delete_and_overwriting_an_existing_destination_are_refused()
    {
        var handler = Handler;
        Assert.Throws<UnauthorizedAccessException>(() => handler.Delete(Args(Allowed)));
        Assert.Throws<UnauthorizedAccessException>(() => handler.Rename(new JsonObject { ["path"] = Allowed, ["newPath"] = Allowed + "-moved" }));
        var first = Path.Combine(Allowed, "first.txt");
        var second = Path.Combine(Allowed, "second.txt");
        File.WriteAllText(first, "first"); File.WriteAllText(second, "second");
        Assert.Throws<IOException>(() => handler.Rename(new JsonObject { ["path"] = first, ["newPath"] = second }));
        Assert.Equal("first", File.ReadAllText(first)); Assert.Equal("second", File.ReadAllText(second));
    }

    static void Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
}
