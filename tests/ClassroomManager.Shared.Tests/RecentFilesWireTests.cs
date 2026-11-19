// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using Xunit;

namespace ClassroomManager.Shared.Tests;

/// <summary>
/// Recent files (T-212), the parts a mutation test found nothing watching (docs/QUALITY.md): the exact names on the wire between Host and Client, the clamps, the
/// edges of what counts as an extension or a safe path, and the lists of ignored folders and noise files. A renamed field or a moved limit must fail here, not in the lab.
/// </summary>
public sealed class RecentFilesWireTests
{
    static readonly DateTime Now = new(2026, 10, 24, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_query_goes_out_under_exactly_these_names_and_in_this_time_format()
    {
        var args = new RecentQuery
        {
            Folders = ["%DESKTOP%", @"D:\Work"], SinceUtc = Now.AddHours(-1), WithinMinutes = 90, UntilUtc = Now, Extensions = [".docx", ".pdf"],
            AllFiles = true, IncludeFolders = true, MaxItems = 50, AfterModifiedUtc = Now.AddMinutes(-5), AfterPath = @"D:\Work\a.txt",
        }.ToArgs();

        Assert.Equal(["afterModifiedUtc", "afterPath", "allFiles", "extensions", "folders", "includeFolders", "maxItems", "sinceUtc", "untilUtc", "withinMinutes"],
            args.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("2026-10-24T14:00:00.0000000Z", (string?)args["sinceUtc"]);
        Assert.Equal("2026-10-24T15:00:00.0000000Z", (string?)args["untilUtc"]);
        Assert.Equal("2026-10-24T14:55:00.0000000Z", (string?)args["afterModifiedUtc"]);
        Assert.Equal(90, (int?)args["withinMinutes"]);
        Assert.True((bool?)args["allFiles"]);
        Assert.True((bool?)args["includeFolders"]);
        Assert.Equal(50, (int?)args["maxItems"]);
        Assert.Equal(@"D:\Work\a.txt", (string?)args["afterPath"]);
        Assert.Equal(["%DESKTOP%", @"D:\Work"], args["folders"]!.AsArray().Select(node => (string?)node));
    }

    [Fact]
    public void A_full_query_survives_the_wire()
    {
        var sent = new RecentQuery
        {
            Folders = ["%DOCUMENTS%", @"D:\Work"], SinceUtc = Now.AddHours(-2), WithinMinutes = 45, UntilUtc = Now, Extensions = [".docx", ".pdf"],
            AllFiles = true, IncludeFolders = true, MaxItems = 77, AfterModifiedUtc = Now.AddMinutes(-3), AfterPath = @"D:\Work\b.txt",
        };
        var read = RecentQuery.FromArgs(sent.ToArgs(), Now);
        Assert.Equal(sent.Folders, read.Folders);
        Assert.Equal(45, read.WithinMinutes);
        Assert.Equal(Now.AddMinutes(-45), read.SinceUtc);   // counted back on the receiver's clock
        Assert.Equal(sent.UntilUtc, read.UntilUtc);
        Assert.Equal(sent.AfterModifiedUtc, read.AfterModifiedUtc);
        Assert.Equal(sent.AfterPath, read.AfterPath);
        Assert.True(read.AllFiles && read.IncludeFolders);
        Assert.Equal(77, read.MaxItems);
        Assert.Equal([".docx", ".pdf"], read.Extensions.Order());
        Assert.All(new[] { read.UntilUtc, read.AfterModifiedUtc }, time => Assert.Equal(DateTimeKind.Utc, time!.Value.Kind));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(60 * 24 * 366, 60 * 24 * 366)]
    [InlineData(60 * 24 * 366 + 1, 60 * 24 * 366)]
    public void The_window_in_minutes_is_kept_between_one_minute_and_a_year(int asked, int used)
    {
        var read = RecentQuery.FromArgs(new JsonObject { ["withinMinutes"] = asked }, Now);
        Assert.Equal(used, read.WithinMinutes);
        Assert.Equal(Now.AddMinutes(-used), read.SinceUtc);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(RecentFiles.MaxItemsLimit, RecentFiles.MaxItemsLimit)]
    [InlineData(RecentFiles.MaxItemsLimit + 1, RecentFiles.MaxItemsLimit)]
    public void The_number_of_items_is_kept_between_one_and_the_limit(int asked, int used) =>
        Assert.Equal(used, RecentQuery.FromArgs(new JsonObject { ["maxItems"] = asked }, Now).MaxItems);

    [Fact]
    public void A_query_without_a_window_gets_the_default_thirty_minutes_the_default_folders_and_the_default_size()
    {
        var read = RecentQuery.FromArgs(new JsonObject(), Now);
        Assert.Equal(Now.AddMinutes(-30), read.SinceUtc);
        Assert.Null(read.WithinMinutes);
        Assert.Equal(["%DESKTOP%", "%DOCUMENTS%"], read.Folders);
        Assert.Equal(1000, read.MaxItems);
        Assert.False(read.AllFiles);
        Assert.False(read.IncludeFolders);
        Assert.Empty(read.Extensions);
        Assert.Equal("", read.AfterPath);
        Assert.Null(read.UntilUtc);
    }

    [Fact]
    public void Folders_are_trimmed_listed_once_whatever_the_case_and_cut_at_twenty()
    {
        var many = new JsonArray(Enumerable.Range(0, 30).Select(i => (JsonNode)$@"C:\Folder{i}").ToArray());
        Assert.Equal(RecentFiles.MaxFolders, RecentQuery.FromArgs(new JsonObject { ["folders"] = many }, Now).Folders.Count);
        var read = RecentQuery.FromArgs(new JsonObject { ["folders"] = new JsonArray(" C:\\A ", "c:\\a", "", "   ", "D:\\B") }, Now);
        Assert.Equal([@"C:\A", @"D:\B"], read.Folders);
    }

    [Fact]
    public void A_time_that_is_not_a_time_is_ignored_and_a_text_where_a_flag_belongs_is_false()
    {
        var read = RecentQuery.FromArgs(new JsonObject { ["sinceUtc"] = "yesterday-ish", ["untilUtc"] = 5, ["allFiles"] = "true", ["includeFolders"] = "yes" }, Now);
        Assert.Equal(Now.AddMinutes(-30), read.SinceUtc);
        Assert.Null(read.UntilUtc);
        Assert.False(read.AllFiles);
        Assert.False(read.IncludeFolders);
    }

    [Fact]
    public void The_window_includes_both_ends_and_a_file_is_wanted_by_extension_unless_all_files_are()
    {
        var query = new RecentQuery { SinceUtc = Now.AddHours(-1), UntilUtc = Now, Extensions = new HashSet<string>([".docx"], StringComparer.OrdinalIgnoreCase) };
        Assert.True(query.InWindow(Now.AddHours(-1)));
        Assert.True(query.InWindow(Now));
        Assert.False(query.InWindow(Now.AddHours(-1).AddTicks(-1)));
        Assert.False(query.InWindow(Now.AddTicks(1)));
        Assert.True(new RecentQuery { SinceUtc = Now.AddHours(-1) }.InWindow(DateTime.MaxValue.AddDays(-1)), "No end means now or later.");
        Assert.True(query.WantsFile("Essay.DOCX"));
        Assert.False(query.WantsFile("essay.pdf"));
        Assert.False(query.WantsFile("README"));
        Assert.True(new RecentQuery { AllFiles = true }.WantsFile("README"));
    }

    // ---- extensions -------------------------------------------------------------------------

    [Theory]
    [InlineData(".a", ".a")]
    [InlineData(".abcdefghijklmnopqrs", ".abcdefghijklmnopqrs")]     // 20 characters with the dot
    [InlineData(".abcdefghijklmnopqrst", "")]                         // 21
    [InlineData("a_b-c.d", ".a_b-c.d")]
    [InlineData(".a+b", "")]
    [InlineData(".a b", "")]
    [InlineData(".a$", "")]
    [InlineData("*.TXT", ".txt")]
    [InlineData("**.txt", "")]
    public void The_edges_of_an_extension(string typed, string expected) => Assert.Equal(expected, FileTypes.NormalizeExtension(typed));

    [Fact]
    public void A_typed_list_gives_each_extension_once_in_the_order_given()
    {
        Assert.Equal([".docx", ".pdf", ".txt"], FileTypes.ParseExtensions("docx, .pdf;*.TXT\tDOCX\r\n c++ , ,"));
        Assert.Empty(FileTypes.ParseExtensions(null));
        Assert.Empty(FileTypes.ParseExtensions("  "));
    }

    [Fact]
    public void The_default_groups_are_named_what_the_Host_shows_hold_only_real_extensions_and_share_none()
    {
        Assert.Equal(["Documents", "Images", "Source code"], FileTypes.Defaults.Select(group => group.Name));
        Assert.Equal("Folders", FileTypes.Folders);
        Assert.Equal("Other files", FileTypes.Other);
        var all = FileTypes.Defaults.SelectMany(group => group.Extensions).ToList();
        Assert.All(all, extension => Assert.Equal(extension, FileTypes.NormalizeExtension(extension)));
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(".docx", FileTypes.Defaults[0].Extensions);
        Assert.Contains(".png", FileTypes.Defaults[1].Extensions);
        Assert.Contains(".cs", FileTypes.Defaults[2].Extensions);
        Assert.Contains(".sb3", FileTypes.Defaults[2].Extensions);   // Scratch projects
    }

    [Fact]
    public void A_file_belongs_to_the_first_group_that_lists_its_extension_in_any_case_or_to_other_files()
    {
        Assert.Equal("Documents", FileTypes.KindOf("Essay.DOCX", FileTypes.Defaults));
        Assert.Equal("Images", FileTypes.KindOf("cat.jpeg", FileTypes.Defaults));
        Assert.Equal("Source code", FileTypes.KindOf("main.PY", FileTypes.Defaults));
        Assert.Equal("Other files", FileTypes.KindOf("archive.zip", FileTypes.Defaults));
        Assert.Equal("Other files", FileTypes.KindOf("README", FileTypes.Defaults));
        Assert.Equal("Mine", FileTypes.KindOf("x.zip", [new("Mine", [".ZIP"])]));
    }

    // ---- paths and noise --------------------------------------------------------------------

    [Theory]
    [InlineData("a.txt", true)]
    [InlineData(@"sub\a.txt", true)]
    [InlineData("sub/a.txt", true)]
    [InlineData("a..b", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData(@"..\a.txt", false)]
    [InlineData("sub/../a.txt", false)]
    [InlineData(@"sub\..\..\a.txt", false)]
    [InlineData(@"C:\a.txt", false)]
    [InlineData(@"\a.txt", false)]
    [InlineData("/a.txt", false)]
    public void A_relative_path_must_stay_inside_its_folder(string path, bool safe) => Assert.Equal(safe, RecentFiles.SafeRelativePath(path));

    [Theory]
    [InlineData(".git", true)]
    [InlineData(".svn", true)]
    [InlineData(".hg", true)]
    [InlineData(".vs", true)]
    [InlineData(".idea", true)]
    [InlineData(".vscode", true)]
    [InlineData("node_modules", true)]
    [InlineData("__pycache__", true)]
    [InlineData(".venv", true)]
    [InlineData("venv", true)]
    [InlineData(".gradle", true)]
    [InlineData(".cache", true)]
    [InlineData("NODE_MODULES", true)]
    [InlineData("src", false)]
    [InlineData("Homework", false)]
    public void Tool_and_version_control_folders_are_never_looked_into(string name, bool skipped) => Assert.Equal(skipped, RecentFiles.SkipFolder(name));

    [Theory]
    [InlineData("~$report.docx", true)]
    [InlineData(".~lock.report.odt#", true)]
    [InlineData(".~LOCK.report.odt#", true)]
    [InlineData("download.tmp", true)]
    [InlineData("movie.CRDOWNLOAD", true)]
    [InlineData("big.part", true)]
    [InlineData("desktop.ini", true)]
    [InlineData("Thumbs.db", true)]
    [InlineData("report.docx", false)]
    [InlineData("a~$b.docx", false)]
    [InlineData("tmp.txt", false)]
    [InlineData("part.txt", false)]
    public void Lock_temporary_and_Explorer_files_are_not_student_work(string name, bool noise) => Assert.Equal(noise, RecentFiles.IsNoise(name));

    // ---- what comes back --------------------------------------------------------------------

    [Fact]
    public void An_entry_goes_back_under_exactly_these_names()
    {
        var entry = new RecentEntry("%DESKTOP%", @"C:\Users\s\Desktop\sub\a.txt", @"sub\a.txt", false, 1234, Now);
        var json = entry.ToJson();
        Assert.Equal(["isFolder", "modifiedUtc", "path", "relativePath", "root", "size"], json.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("2026-10-24T15:00:00.0000000Z", (string?)json["modifiedUtc"]);
        var back = RecentEntry.FromJson(json)!;
        Assert.Equal(entry, back);
        Assert.True(RecentEntry.FromJson(new RecentEntry("r", @"C:\x", "x", true, 0, Now).ToJson())!.IsFolder);
    }

    [Fact]
    public void An_entry_that_is_malformed_or_would_leave_its_folder_is_dropped_and_a_negative_size_is_zero()
    {
        JsonObject Entry(string path = @"C:\x\a.txt", string relative = "a.txt", string modified = "2026-10-24T15:00:00.0000000Z", long size = 5) =>
            new() { ["root"] = "r", ["path"] = path, ["relativePath"] = relative, ["isFolder"] = false, ["size"] = size, ["modifiedUtc"] = modified };
        Assert.NotNull(RecentEntry.FromJson(Entry()));
        Assert.Null(RecentEntry.FromJson(null));
        Assert.Null(RecentEntry.FromJson(JsonValue.Create("text")));
        Assert.Null(RecentEntry.FromJson(Entry(path: "")));
        Assert.Null(RecentEntry.FromJson(Entry(relative: @"..\secret.txt")));
        Assert.Null(RecentEntry.FromJson(Entry(relative: "")));
        Assert.Null(RecentEntry.FromJson(Entry(modified: "not a time")));
        Assert.Equal(0, RecentEntry.FromJson(Entry(size: -9))!.Size);
        Assert.Equal(DateTimeKind.Utc, RecentEntry.FromJson(Entry())!.ModifiedUtc.Kind);
    }

    [Fact]
    public void A_reply_goes_back_under_exactly_these_names_and_a_reply_missing_parts_reads_as_empty()
    {
        var scan = new RecentScan([new RecentEntry("r", @"C:\a", "a", false, 1, Now)], true, [@"C:\Gone"]) { SupportsPaging = true, HasMore = true, Errors = ["unreadable"] };
        var json = scan.ToJson();
        Assert.Equal(["errors", "hasMore", "items", "missing", "supportsPaging", "truncated"], json.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        var back = RecentScan.FromJson(json);
        Assert.True(back.Truncated && back.SupportsPaging && back.HasMore);
        Assert.Equal([@"C:\Gone"], back.Missing);
        Assert.Equal(["unreadable"], back.Errors);
        Assert.Single(back.Items);

        var empty = RecentScan.FromJson(new JsonObject());
        Assert.Empty(empty.Items);
        Assert.Empty(empty.Missing);
        Assert.Empty(empty.Errors);
        Assert.False(empty.Truncated || empty.SupportsPaging || empty.HasMore);

        var noisy = RecentScan.FromJson(new JsonObject { ["missing"] = new JsonArray("", @"C:\Gone"), ["items"] = new JsonArray(JsonValue.Create("bad"), new RecentEntry("r", @"C:\a", "a", false, 1, Now).ToJson()) });
        Assert.Equal([@"C:\Gone"], noisy.Missing);
        Assert.Single(noisy.Items);
    }

    [Fact]
    public void The_limits_of_a_scan_are_what_the_documents_say()
    {
        Assert.Equal("1.19", RecentFiles.RequiredVersion);
        Assert.Equal((30, 1000, 5000, 20, 12, 200_000), (RecentFiles.DefaultMinutes, RecentFiles.DefaultMaxItems, RecentFiles.MaxItemsLimit, RecentFiles.MaxFolders, RecentFiles.MaxDepth, RecentFiles.MaxVisited));
        Assert.Equal(["%DESKTOP%", "%DOCUMENTS%"], RecentFiles.DefaultFolders);
    }
}
