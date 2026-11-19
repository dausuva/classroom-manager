// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>
/// Recent files (T-212) over a real connection: the Client's own handlers for <see cref="Cmd.FsRecent"/> and <see cref="Cmd.FsRead"/>
/// behind a loopback server, and the Host's copier in front of it.
/// </summary>
[Collection("KeyRefusalPause")]
public sealed class RecentFilesEndToEndTests : IDisposable
{
    const string Key = "synthetic-recent-files-key";
    readonly string _root = Path.Combine(Path.GetTempPath(), "cm-recent-e2e-" + Guid.NewGuid().ToString("N"));
    readonly string _desktop;
    readonly string _copies;
    readonly ClientInfo _client = new() { ComputerName = "410-05", ClientId = "c1", User = "jonas" };

    public RecentFilesEndToEndTests()
    {
        KeyRefusalPause.ClearAll();
        _desktop = Path.Combine(_root, "Student", "Desktop");
        _copies = Path.Combine(_root, "Copies");
        Directory.CreateDirectory(_desktop);
    }

    public void Dispose()
    {
        KeyRefusalPause.ClearAll();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    async Task<T> WithClientAsync<T>(Func<ClientSession, CancellationToken, Task<T>> test, int timeoutSeconds = 60,
        ConnectionLimits? limits = null)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var files = new FileHandler(new ClientConfig { AllowedRoots = [_root] });
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var server = new CommandServer(new ClientConfig { UseTls = false }, new KeyStore(Key, []),
            async (request, _, _, ct) => request.Cmd switch
            {
                Cmd.FsRecent => DeliveryHandler.Recent(request.Args, DateTime.UtcNow),
                Cmd.FsRead => await files.ReadAsync(request.Args, ct),
                _ => Response.Unsupported(request.Cmd),
            },
            new PreAuthGate((limits ?? new ConnectionLimits()) with { FailuresBeforeBackoff = 100 }));
        listener.Start();
        var accepting = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) _ = server.HandleAsync(await listener.AcceptTcpClientAsync(stop.Token), stop.Token); }
            catch (OperationCanceledException) { }
        });
        try
        {
            var info = new ClientInfo { Address = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
            using var session = await ClientSession.OpenAsync(info, new HostSettings { UseTls = false, Psk = Key }, stop.Token);
            return await test(session, stop.Token);
        }
        finally { stop.Cancel(); await accepting; listener.Stop(); }
    }

    string Write(string relative, string text, DateTime modifiedUtc)
    {
        var path = Path.Combine(_desktop, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return path;
    }

    RecentQuery Fresh(bool allFiles = false, bool folders = false) => new()
    {
        Folders = [_desktop], SinceUtc = DateTime.UtcNow.AddMinutes(-30), WithinMinutes = 30, Extensions = [".docx", ".py"],
        AllFiles = allFiles, IncludeFolders = folders,
    };

    string CopyOf(string relative) => Path.Combine(_copies, "410-05_jonas", "Desktop", relative);

    [Fact]
    public async Task Backup_pages_beyond_5000_equal_timestamps_and_restores_every_file()
    {
        const int fileCount = 5003;
        var modified = DateTime.UtcNow.AddMinutes(-2);
        for (int i = 0; i < fileCount; i++) Write($"work-{i:D5}.txt", $"Synthetic work {i}", modified);
        var committed = false;
        await WithClientAsync(async (session, ct) => await StudentBackup.RunWorkflowAsync(
            new RecentQuery { Folders = [_desktop], AllFiles = true, SinceUtc = StudentBackup.Never, MaxItems = 5000 },
            (query, token) => RecentFilesCopier.ListAsync(session, query, token),
            (entries, token) => RecentFilesCopier.CopyAsync(session, _client, entries, _copies, true, ct: token),
            _ => committed = true, TimeProvider.System, ct), timeoutSeconds: 240,
            // This verifies pagination/copy completeness, not request throttling. Give the synthetic
            // tiny-file workload a fixed budget so a fast CI disk need not wait for wall-clock refill.
            limits: new ConnectionLimits { RequestBurst = fileCount + 16, RequestsPerSecond = 0 });
        Assert.True(committed);
        var restored = Path.Combine(_root, "Restored");
        Directory.CreateDirectory(restored);
        for (int i = 0; i < fileCount; i++)
        {
            var name = $"work-{i:D5}.txt";
            File.Copy(CopyOf(name), Path.Combine(restored, name));
            Assert.Equal(File.ReadAllBytes(Path.Combine(_desktop, name)), File.ReadAllBytes(Path.Combine(restored, name)));
        }
        Assert.Equal(fileCount, Directory.GetFiles(restored).Length);
    }

    [Fact]
    public async Task The_host_lists_only_fresh_files_of_the_asked_types()
    {
        var now = DateTime.UtcNow;
        Write("Essay.docx", "essay", now.AddMinutes(-5));
        Write("old.docx", "old", now.AddHours(-5));
        Write("setup.exe", "exe", now.AddMinutes(-1));
        Write(@"Game\main.py", "print(1)", now.AddMinutes(-2));

        var scan = await WithClientAsync((session, ct) => RecentFilesCopier.ListAsync(session, Fresh(), ct));

        Assert.Equal([@"Game\main.py", "Essay.docx"], scan.Items.Select(item => item.RelativePath));
        Assert.All(scan.Items, item => Assert.Equal(_desktop, item.Root));
        Assert.Empty(scan.Missing);
    }

    [Fact]
    public async Task Copies_land_per_computer_with_their_change_time_and_an_unchanged_file_is_not_sent_again()
    {
        var modified = DateTime.UtcNow.AddMinutes(-3);
        Write("Essay.docx", "first draft", modified);

        var (first, second) = await WithClientAsync(async (session, ct) =>
        {
            var items = (await RecentFilesCopier.ListAsync(session, Fresh(), ct)).Items;
            var one = await RecentFilesCopier.CopyAsync(session, _client, items, _copies, overwrite: true, ct: ct);
            var two = await RecentFilesCopier.CopyAsync(session, _client, items, _copies, overwrite: true, ct: ct);
            return (one, two);
        });

        Assert.Equal((1, 0), (first.Copied, first.UpToDate));
        Assert.Empty(first.Failed);
        Assert.Equal("first draft", File.ReadAllText(CopyOf("Essay.docx")));
        Assert.InRange(Math.Abs((File.GetLastWriteTimeUtc(CopyOf("Essay.docx")) - modified).TotalSeconds), 0, 1);
        Assert.Equal([(CopyOf("Essay.docx"), first.Written[0].ModifiedUtc)], first.Written);
        Assert.Equal((0, 1), (second.Copied, second.UpToDate));
    }

    [Fact]
    public async Task A_changed_file_replaces_the_earlier_copy_unless_overwriting_is_off()
    {
        Write("Essay.docx", "first draft", DateTime.UtcNow.AddMinutes(-10));
        await WithClientAsync(async (session, ct) =>
            await RecentFilesCopier.CopyAsync(session, _client, (await RecentFilesCopier.ListAsync(session, Fresh(), ct)).Items, _copies, true, ct: ct));
        Write("Essay.docx", "second draft, longer", DateTime.UtcNow.AddMinutes(-1));

        var kept = await WithClientAsync(async (session, ct) =>
            await RecentFilesCopier.CopyAsync(session, _client, (await RecentFilesCopier.ListAsync(session, Fresh(), ct)).Items, _copies, false, ct: ct));
        Assert.Equal((0, 1), (kept.Copied, kept.UpToDate));
        Assert.Equal("first draft", File.ReadAllText(CopyOf("Essay.docx")));

        var replaced = await WithClientAsync(async (session, ct) =>
            await RecentFilesCopier.CopyAsync(session, _client, (await RecentFilesCopier.ListAsync(session, Fresh(), ct)).Items, _copies, true, ct: ct));
        Assert.Equal(1, replaced.Copied);
        Assert.Equal("second draft, longer", File.ReadAllText(CopyOf("Essay.docx")));
    }

    [Fact]
    public async Task A_copied_folder_brings_everything_in_it_also_older_files()
    {
        var now = DateTime.UtcNow;
        Write(@"Project\new.py", "new", now.AddMinutes(-1));
        Write(@"Project\lib\old.txt", "old", now.AddDays(-30));
        Directory.SetLastWriteTimeUtc(Path.Combine(_desktop, "Project", "lib"), now.AddDays(-30));
        Directory.SetLastWriteTimeUtc(Path.Combine(_desktop, "Project"), now.AddMinutes(-1));

        var outcome = await WithClientAsync(async (session, ct) =>
        {
            var scan = await RecentFilesCopier.ListAsync(session, Fresh(folders: true), ct);
            var folder = Assert.Single(scan.Items, item => item.IsFolder);
            Assert.Equal("Project", folder.RelativePath);
            return await RecentFilesCopier.CopyAsync(session, _client, [folder], _copies, true, ct: ct);
        });

        Assert.Equal(2, outcome.Copied);
        Assert.Empty(outcome.Failed);
        Assert.Equal("new", File.ReadAllText(CopyOf(@"Project\new.py")));
        Assert.Equal("old", File.ReadAllText(CopyOf(@"Project\lib\old.txt")));
    }

    [Fact]
    public async Task A_file_gone_before_its_copy_is_named_and_the_earlier_copy_and_the_rest_are_kept()
    {
        var now = DateTime.UtcNow;
        var vanishing = Write("Essay.docx", "kept copy", now.AddMinutes(-5));
        Write("Other.docx", "other", now.AddMinutes(-4));
        await WithClientAsync(async (session, ct) =>
            await RecentFilesCopier.CopyAsync(session, _client, (await RecentFilesCopier.ListAsync(session, Fresh(), ct)).Items, _copies, true, ct: ct));

        var outcome = await WithClientAsync(async (session, ct) =>
        {
            File.WriteAllText(vanishing, "changed then deleted");
            File.SetLastWriteTimeUtc(vanishing, now.AddMinutes(-1));
            var items = (await RecentFilesCopier.ListAsync(session, Fresh(), ct)).Items;
            File.Delete(vanishing);
            return await RecentFilesCopier.CopyAsync(session, _client, items, _copies, true, ct: ct);
        });

        var problem = Assert.Single(outcome.Failed);
        Assert.Contains("Essay.docx", problem);
        Assert.Contains("File not found", problem);
        Assert.Equal("kept copy", File.ReadAllText(CopyOf("Essay.docx")));
        Assert.Equal(1, outcome.UpToDate);
        Assert.Empty(Directory.GetFiles(_copies, "*.cm-partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_missing_watched_folder_is_reported_and_not_an_error()
    {
        var scan = await WithClientAsync((session, ct) => RecentFilesCopier.ListAsync(session,
            Fresh() with { Folders = [Path.Combine(_root, "Nope"), _desktop] }, ct));
        Assert.Equal([Path.Combine(_root, "Nope")], scan.Missing);
    }

    /// <summary>
    /// The computer went to sleep in the middle of a backup (seen on the test PC .205, 2026-10-25): the copy stops at the lost connection
    /// instead of marking every remaining file as failed, and keeps what it copied.
    /// </summary>
    [Fact]
    public async Task A_connection_lost_in_the_middle_ends_the_copy_and_keeps_what_was_copied()
    {
        var now = DateTime.UtcNow;
        Write("a.docx", "first", now.AddMinutes(-3));
        Write("b.docx", "second", now.AddMinutes(-2));
        Write("c.docx", "third", now.AddMinutes(-1));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var files = new FileHandler(new ClientConfig { AllowedRoots = [_root] });
        var reads = 0;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var server = new CommandServer(new ClientConfig { UseTls = false }, new KeyStore(Key, []),
            async (request, _, _, ct) =>
            {
                if (request.Cmd == Cmd.FsRead && Interlocked.Increment(ref reads) == 2)
                {
                    // The second file: the computer is gone before it answers.
                    stop.Cancel();
                    listener.Stop();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return request.Cmd switch
                {
                    Cmd.FsRecent => DeliveryHandler.Recent(request.Args, DateTime.UtcNow),
                    Cmd.FsRead => await files.ReadAsync(request.Args, ct),
                    _ => Response.Unsupported(request.Cmd),
                };
            },
            new PreAuthGate(new ConnectionLimits { FailuresBeforeBackoff = 100 }));
        listener.Start();
        var accepting = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) _ = server.HandleAsync(await listener.AcceptTcpClientAsync(stop.Token), stop.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        });
        var info = new ClientInfo { Address = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        using var session = await ClientSession.OpenAsync(info, new HostSettings { UseTls = false, Psk = Key }, stop.Token);
        var items = (await RecentFilesCopier.ListAsync(session, Fresh(), stop.Token)).Items.OrderBy(item => item.ModifiedUtc).ToList();

        await Assert.ThrowsAnyAsync<Exception>(() => RecentFilesCopier.CopyAsync(session, _client, items, _copies, overwrite: true));

        Assert.Equal("first", File.ReadAllText(CopyOf("a.docx")));
        Assert.False(File.Exists(CopyOf("b.docx")));
        Assert.Empty(Directory.GetFiles(_copies, "*.cm-partial", SearchOption.AllDirectories));
        await accepting;
    }
}
