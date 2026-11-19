// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>Recent files (T-212): the type groups, the query on the wire, and the scan of the watched folders.</summary>
public sealed class RecentFilesTests : IDisposable
{
    static readonly DateTime Now = new(2026, 10, 24, 15, 0, 0, DateTimeKind.Utc);
    readonly string _root = Path.Combine(Path.GetTempPath(), "cm-recent-" + Guid.NewGuid().ToString("N"));

    public RecentFilesTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Pagination_cursor_and_completeness_survive_the_wire()
    {
        var query = new RecentQuery { SinceUtc = Now.AddDays(-1), AfterModifiedUtc = Now, AfterPath = @"C:\Desktop\a.txt" };
        var read = RecentQuery.FromArgs(query.ToArgs(), Now);
        Assert.Equal(query.AfterModifiedUtc, read.AfterModifiedUtc);
        Assert.Equal(query.AfterPath, read.AfterPath);
        var scan = new RecentScan([], true, ["missing"]) { SupportsPaging = true, HasMore = true, Errors = ["unreadable"] };
        var reply = RecentScan.FromJson(scan.ToJson());
        Assert.True(reply.SupportsPaging && reply.HasMore);
        Assert.Equal(scan.Errors, reply.Errors);
    }

    public void Dispose()
    {
        try
        {
            // Links first (only the link goes, not what it points to), then the hidden entries made visible so they can be deleted.
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
            foreach (var link in new DirectoryInfo(_root).EnumerateDirectories("*", options).Where(dir => dir.LinkTarget is not null).ToList())
                link.Delete();
            foreach (var entry in new DirectoryInfo(_root).EnumerateFileSystemInfos("*", options))
                entry.Attributes = entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal;
            Directory.Delete(_root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- type groups ------------------------------------------------------------------------

    [Theory]
    [InlineData("docx", ".docx")]
    [InlineData(".DOCX", ".docx")]
    [InlineData("*.Pdf", ".pdf")]
    [InlineData("  .txt  ", ".txt")]
    [InlineData("tar.gz", ".tar.gz")]
    [InlineData("c++", "")]
    [InlineData("my file", "")]
    [InlineData(@"C:\x.docx", "")]
    [InlineData("*", "")]
    [InlineData(".", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("abcdefghijklmnopqrstu", "")]
    public void An_extension_is_normalised_or_refused(string? typed, string expected) =>
        Assert.Equal(expected, FileTypes.NormalizeExtension(typed));

    [Fact]
    public void A_typed_list_keeps_each_extension_once_in_order_and_drops_the_rest()
    {
        Assert.Equal([".docx", ".pdf", ".txt"], FileTypes.ParseExtensions("docx, .PDF; *.txt\n docx  bad/one"));
        Assert.Empty(FileTypes.ParseExtensions(null));
    }

    [Theory]
    [InlineData("Essay.DOCX", FileTypes.Documents)]
    [InlineData("photo.jpeg", FileTypes.Images)]
    [InlineData("game.sb3", FileTypes.SourceCode)]
    [InlineData("main.py", FileTypes.SourceCode)]
    [InlineData("setup.exe", FileTypes.Other)]
    [InlineData("README", FileTypes.Other)]
    public void A_file_belongs_to_the_first_group_that_lists_its_extension(string name, string kind) =>
        Assert.Equal(kind, FileTypes.KindOf(name, FileTypes.Defaults));

    [Fact]
    public void An_edited_group_list_decides_the_kind()
    {
        var groups = new[] { new FileTypeGroup("Python", [".py"]), new FileTypeGroup(FileTypes.SourceCode, [".py", ".cs"]) };
        Assert.Equal("Python", FileTypes.KindOf("a.py", groups));
        Assert.Equal(FileTypes.SourceCode, FileTypes.KindOf("a.cs", groups));
    }

    [Fact]
    public void The_default_groups_share_no_extension_and_every_one_is_normalised()
    {
        var all = FileTypes.Defaults.SelectMany(group => group.Extensions).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, ext => Assert.Equal(ext, FileTypes.NormalizeExtension(ext)));
    }

    // ---- the query on the wire --------------------------------------------------------------

    [Fact]
    public void A_query_reads_back_as_it_was_sent()
    {
        var sent = new RecentQuery
        {
            Folders = ["%DESKTOP%", @"D:\Projects"],
            SinceUtc = Now.AddHours(-2),
            UntilUtc = Now.AddHours(-1),
            Extensions = [".docx", ".py"],
            IncludeFolders = true,
            MaxItems = 50,
        };
        var read = RecentQuery.FromArgs(JsonNode.Parse(sent.ToArgs().ToJsonString())!.AsObject(), Now);
        Assert.Equal(sent.Folders, read.Folders);
        Assert.Equal(sent.SinceUtc, read.SinceUtc);
        Assert.Equal(sent.UntilUtc, read.UntilUtc);
        Assert.Equal(DateTimeKind.Utc, read.SinceUtc.Kind);
        Assert.Equal([".docx", ".py"], read.Extensions.Order(StringComparer.Ordinal));
        Assert.True(read.IncludeFolders);
        Assert.False(read.AllFiles);
        Assert.Equal(50, read.MaxItems);
    }

    [Fact]
    public void A_local_time_is_sent_as_utc()
    {
        var local = new DateTime(2026, 10, 24, 18, 0, 0, DateTimeKind.Local);
        var read = RecentQuery.FromArgs(new RecentQuery { SinceUtc = local }.ToArgs(), Now);
        Assert.Equal(local.ToUniversalTime(), read.SinceUtc);
    }

    [Fact]
    public void Without_arguments_the_default_folders_and_the_last_30_minutes_are_used()
    {
        var read = RecentQuery.FromArgs(new JsonObject(), Now);
        Assert.Equal(RecentFiles.DefaultFolders, read.Folders);
        Assert.Equal(Now.AddMinutes(-30), read.SinceUtc);
        Assert.Null(read.UntilUtc);
        Assert.Equal(RecentFiles.DefaultMaxItems, read.MaxItems);
        Assert.False(read.AllFiles);
        Assert.False(read.IncludeFolders);
    }

    [Fact]
    public void Within_minutes_counts_back_from_the_clients_clock_and_wins_over_since()
    {
        var read = RecentQuery.FromArgs(new JsonObject { ["withinMinutes"] = 10, ["sinceUtc"] = "2020-01-01T00:00:00Z" }, Now);
        Assert.Equal(Now.AddMinutes(-10), read.SinceUtc);
        Assert.Equal(Now.AddMinutes(-1), RecentQuery.FromArgs(new JsonObject { ["withinMinutes"] = -5 }, Now).SinceUtc);
        // Sent with a since time from the Host's clock, the Client still counts on its own.
        var hostClockAhead = new RecentQuery { SinceUtc = Now.AddMinutes(-25), WithinMinutes = 30 }.ToArgs();
        Assert.Equal(Now.AddMinutes(-30), RecentQuery.FromArgs(hostClockAhead, Now).SinceUtc);
        Assert.Equal(30, RecentQuery.FromArgs(hostClockAhead, Now).WithinMinutes);
        Assert.Null(RecentQuery.FromArgs(new RecentQuery { SinceUtc = Now }.ToArgs(), Now).WithinMinutes);
    }

    [Fact]
    public void Bad_values_fall_back_and_limits_hold()
    {
        var folders = new JsonArray(Enumerable.Range(0, 40).Select(i => (JsonNode)$@"C:\F{i}").Append(" ").Append(@"c:\f1").ToArray());
        var read = RecentQuery.FromArgs(new JsonObject
        {
            ["folders"] = folders, ["sinceUtc"] = "not a date", ["maxItems"] = 999_999,
            ["extensions"] = new JsonArray("DOCX", "bad name", "*.txt"),
        }, Now);
        Assert.Equal(RecentFiles.MaxFolders, read.Folders.Count);
        Assert.DoesNotContain(" ", read.Folders);
        Assert.Equal(Now.AddMinutes(-30), read.SinceUtc);
        Assert.Equal(RecentFiles.MaxItemsLimit, read.MaxItems);
        Assert.Equal([".docx", ".txt"], read.Extensions.Order(StringComparer.Ordinal));
        Assert.Equal(1, RecentQuery.FromArgs(new JsonObject { ["maxItems"] = 0 }, Now).MaxItems);
    }

    [Fact]
    public void The_window_includes_both_ends()
    {
        var query = new RecentQuery { SinceUtc = Now.AddMinutes(-30), UntilUtc = Now };
        Assert.True(query.InWindow(Now.AddMinutes(-30)));
        Assert.True(query.InWindow(Now));
        Assert.False(query.InWindow(Now.AddMinutes(-30).AddTicks(-1)));
        Assert.False(query.InWindow(Now.AddTicks(1)));
        Assert.True(new RecentQuery { SinceUtc = Now }.InWindow(Now.AddYears(1)));
    }

    // ---- replies ----------------------------------------------------------------------------

    [Fact]
    public void A_reply_reads_back_and_entries_that_would_leave_their_folder_are_dropped()
    {
        var good = new RecentEntry("%DESKTOP%", @"C:\Users\a\Desktop\x\Essay.docx", @"x\Essay.docx", false, 1234, Now);
        var scan = new RecentScan([good], true, ["%DOCUMENTS%"]);
        var data = JsonNode.Parse(scan.ToJson().ToJsonString())!.AsObject();
        var items = (JsonArray)data["items"]!;
        items.Add(new JsonObject { ["path"] = @"C:\evil", ["relativePath"] = @"..\..\Windows\evil", ["modifiedUtc"] = "2026-10-24T15:00:00Z" });
        items.Add(new JsonObject { ["path"] = @"C:\evil", ["relativePath"] = @"C:\Windows\evil", ["modifiedUtc"] = "2026-10-24T15:00:00Z" });
        items.Add(new JsonObject { ["path"] = "", ["relativePath"] = "a.txt", ["modifiedUtc"] = "2026-10-24T15:00:00Z" });
        items.Add(new JsonObject { ["path"] = @"C:\a.txt", ["relativePath"] = "a.txt", ["modifiedUtc"] = "yesterday" });
        items.Add(JsonValue.Create(5));

        var read = RecentScan.FromJson(data);
        Assert.Equal([good], read.Items);
        Assert.Equal(DateTimeKind.Utc, read.Items[0].ModifiedUtc.Kind);
        Assert.True(read.Truncated);
        Assert.Equal(["%DOCUMENTS%"], read.Missing);
        Assert.Empty(RecentScan.FromJson(new JsonObject()).Items);
    }

    // ---- the scan ---------------------------------------------------------------------------

    [Fact]
    public void Only_files_changed_in_the_window_are_listed_newest_first()
    {
        var desktop = Folder("Desktop");
        File(desktop, "old.docx", Now.AddHours(-3));
        File(desktop, "fresh.docx", Now.AddMinutes(-10));
        File(desktop, @"Project\fresher.docx", Now.AddMinutes(-2));
        File(desktop, "future.docx", Now.AddMinutes(5));

        var scan = Scan(new RecentQuery { SinceUtc = Now.AddMinutes(-30), UntilUtc = Now, Extensions = [".docx"] }, ("%DESKTOP%", desktop));

        Assert.Equal([@"Project\fresher.docx", "fresh.docx"], scan.Items.Select(item => item.RelativePath));
        Assert.All(scan.Items, item => Assert.Equal("%DESKTOP%", item.Root));
        Assert.Equal(Path.Combine(desktop, "Project", "fresher.docx"), scan.Items[0].Path);
        Assert.Equal(Now.AddMinutes(-2), scan.Items[0].ModifiedUtc);
        Assert.Equal(5, scan.Items[0].Size);
        Assert.False(scan.Truncated);
        Assert.Empty(scan.Missing);
    }

    [Fact]
    public void Extensions_choose_the_files_unless_every_file_is_asked_for()
    {
        var desktop = Folder("Desktop");
        File(desktop, "a.docx", Now);
        File(desktop, "b.PNG", Now);
        File(desktop, "c.exe", Now);
        File(desktop, "noextension", Now);

        Assert.Equal(["b.PNG"], Names(Scan(Window(extensions: [".png"]), ("d", desktop))));
        Assert.Empty(Scan(Window(extensions: []), ("d", desktop)).Items);
        Assert.Equal(["a.docx", "b.PNG", "c.exe", "noextension"], Names(Scan(Window(allFiles: true), ("d", desktop))));
    }

    [Fact]
    public void Lock_and_temporary_files_are_not_work()
    {
        var desktop = Folder("Desktop");
        foreach (var name in new[] { "~$Essay.docx", ".~lock.Essay.odt#", "x.tmp", "big.iso.crdownload", "y.part", "desktop.ini", "Thumbs.db", "Essay.docx" })
            File(desktop, name, Now);
        Assert.Equal(["Essay.docx"], Names(Scan(Window(allFiles: true), ("d", desktop))));
    }

    [Fact]
    public void Tool_and_version_control_folders_are_not_looked_into()
    {
        var documents = Folder("Documents");
        File(documents, @"app\main.js", Now);
        File(documents, @"app\node_modules\lib\index.js", Now);
        File(documents, @"app\.git\config.json", Now);
        File(documents, @"app\__pycache__\m.py", Now);
        Assert.Equal([@"app\main.js"], Scan(Window(allFiles: true), ("d", documents)).Items.Select(item => item.RelativePath));
    }

    [Fact]
    public void Folders_are_listed_only_when_asked_and_their_files_still_count()
    {
        var desktop = Folder("Desktop");
        File(desktop, @"Project\a.py", Now.AddMinutes(-1));
        Directory.SetLastWriteTimeUtc(Path.Combine(desktop, "Project"), Now.AddMinutes(-1));
        Directory.CreateDirectory(Path.Combine(desktop, "Old"));
        Directory.SetLastWriteTimeUtc(Path.Combine(desktop, "Old"), Now.AddDays(-3));

        var without = Scan(Window(extensions: [".py"]), ("d", desktop));
        Assert.Equal([@"Project\a.py"], without.Items.Select(item => item.RelativePath));

        var with = Scan(Window(extensions: [".py"], folders: true), ("d", desktop));
        Assert.Contains(with.Items, item => item.IsFolder && item.RelativePath == "Project" && item.Size == 0);
        Assert.DoesNotContain(with.Items, item => item.RelativePath == "Old");
        Assert.Contains(with.Items, item => !item.IsFolder && item.RelativePath == @"Project\a.py");
    }

    [Fact]
    public void A_missing_folder_is_named_and_the_others_are_still_scanned()
    {
        var desktop = Folder("Desktop");
        File(desktop, "a.txt", Now);
        var scan = Scan(Window(allFiles: true), ("%DOCUMENTS%", Path.Combine(_root, "nope")), ("%DESKTOP%", desktop), ("bad", "\0"));
        Assert.Equal(["%DOCUMENTS%", "bad"], scan.Missing);
        Assert.Equal(["a.txt"], Names(scan));
    }

    [Fact]
    public void A_folder_named_twice_or_inside_another_is_listed_once()
    {
        var profile = Folder("Profile");
        var documents = Folder(@"Profile\Documents");
        File(documents, "a.txt", Now);
        var scan = Scan(Window(allFiles: true), ("%DOCUMENTS%", documents), ("%USERPROFILE%", profile), ("again", documents + "\\"));
        var only = Assert.Single(scan.Items);
        Assert.Equal("%DOCUMENTS%", only.Root);
        Assert.Equal("a.txt", only.RelativePath);
    }

    [Fact]
    public void A_long_list_keeps_the_newest_and_says_it_was_cut()
    {
        var desktop = Folder("Desktop");
        for (int i = 0; i < 10; i++) File(desktop, $"f{i}.txt", Now.AddMinutes(-i));
        var scan = Scan(Window(allFiles: true) with { MaxItems = 3 }, ("d", desktop));
        Assert.Equal(["f0.txt", "f1.txt", "f2.txt"], Names(scan));
        Assert.True(scan.Truncated);
        Assert.False(Scan(Window(allFiles: true) with { MaxItems = 10 }, ("d", desktop)).Truncated);
    }

    [Fact]
    public void The_scan_stops_at_the_depth_limit()
    {
        var desktop = Folder("Desktop");
        var deep = string.Join('\\', Enumerable.Range(0, RecentFiles.MaxDepth).Select(i => $"d{i}"));
        File(desktop, deep + @"\too-deep.txt", Now);
        File(desktop, string.Join('\\', Enumerable.Range(0, RecentFiles.MaxDepth - 1).Select(i => $"d{i}")) + @"\deepest.txt", Now);
        Assert.Equal(["deepest.txt"], Names(Scan(Window(allFiles: true), ("d", desktop))));
    }

    [Fact]
    public void Hidden_and_system_entries_are_left_out()
    {
        if (!OperatingSystem.IsWindows()) return;
        var desktop = Folder("Desktop");
        var hidden = File(desktop, "hidden.txt", Now);
        System.IO.File.SetAttributes(hidden, FileAttributes.Hidden);
        var system = File(desktop, "system.txt", Now);
        System.IO.File.SetAttributes(system, FileAttributes.System);
        File(desktop, @"Secret\inside.txt", Now);
        new DirectoryInfo(Path.Combine(desktop, "Secret")).Attributes |= FileAttributes.Hidden;
        File(desktop, "shown.txt", Now);
        Assert.Equal(["shown.txt"], Names(Scan(Window(allFiles: true), ("d", desktop))));
    }

    [Fact]
    public void A_junction_is_not_followed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var desktop = Folder("Desktop");
        var elsewhere = Folder("Elsewhere");
        File(elsewhere, "outside.txt", Now);
        File(desktop, "inside.txt", Now);
        var link = Path.Combine(desktop, "Link");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{elsewhere}\"")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            mklink.WaitForExit();
        Assert.True(Directory.Exists(link), "The junction could not be made.");
        Assert.Equal(["inside.txt"], Names(Scan(Window(allFiles: true, folders: true), ("d", desktop))));
    }

    // ---- protocol ---------------------------------------------------------------------------

    [Fact]
    public void Recent_files_is_a_teacher_command_of_the_users_session_from_protocol_1_19()
    {
        Assert.Equal(OperatorRole.Teacher, CommandAccess.RequiredRole(Cmd.FsRecent));
        Assert.False(CommandAccess.IsAllowed(OperatorRole.Observer, Cmd.FsRecent));
        Assert.True(Cmd.IsUserOnly(Cmd.FsRecent));
        Assert.Equal("1.19", Protocol.RequiredVersion(Cmd.FsRecent));
        Assert.Contains(Cmd.FsRecent, Protocol.Capabilities);
        Assert.True(Protocol.IsAtLeast(Protocol.Version, RecentFiles.RequiredVersion));
        // A Client that advertises its commands but not this one, and an older one that advertises nothing, are not asked.
        Assert.False(Protocol.SupportsCommand(Cmd.FsRecent, "1.19", [Cmd.FsFind], capabilitiesAdvertised: true));
        Assert.False(Protocol.SupportsCommand(Cmd.FsRecent, "1.18", [], capabilitiesAdvertised: false));
        Assert.True(Protocol.SupportsCommand(Cmd.FsRecent, "1.19", Protocol.Capabilities, capabilitiesAdvertised: true));
    }

    // ---- helpers ----------------------------------------------------------------------------

    static RecentQuery Window(string[]? extensions = null, bool allFiles = false, bool folders = false) => new()
    {
        SinceUtc = Now.AddMinutes(-30), UntilUtc = Now.AddMinutes(1), Extensions = extensions ?? [], AllFiles = allFiles, IncludeFolders = folders,
    };

    static RecentScan Scan(RecentQuery query, params (string Name, string Path)[] roots) => RecentFiles.Scan(roots, query);

    static List<string> Names(RecentScan scan) => scan.Items.Select(item => Path.GetFileName(item.Path)).Order(StringComparer.Ordinal).ToList();

    string Folder(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    static string File(string folder, string relative, DateTime modifiedUtc)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "hello");
        System.IO.File.SetLastWriteTimeUtc(path, modifiedUtc);
        return path;
    }
}
