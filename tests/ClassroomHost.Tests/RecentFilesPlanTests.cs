// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>Recent files (T-212) on the Host: watched folders per computer, what is asked for, where copies go and how long they stay.</summary>
public sealed class RecentFilesPlanTests
{
    static readonly DateTime NowUtc = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    const string Destination = @"D:\Recent files";

    static ClientInfo Computer(string name = "410-05", string id = "c1", string user = "") =>
        new() { ComputerName = name, ClientId = id, Address = "192.168.1.5", User = user };

    static RecentEntry Entry(string root, string relative, bool folder = false, long size = 10, DateTime? modified = null) =>
        new(root, @"C:\Users\s\" + relative, relative, folder, size, modified ?? NowUtc);

    // ---- watched folders --------------------------------------------------------------------

    [Fact]
    public void A_computer_is_known_by_its_client_id_and_by_its_address_before_it_has_one()
    {
        Assert.Equal("c1", RecentFilesPlan.ComputerKey(Computer()));
        Assert.Equal("ab12", RecentFilesPlan.ComputerKey(Computer(id: "AB12")));
        Assert.Equal("192.168.1.5", RecentFilesPlan.ComputerKey(Computer(id: "")));
    }

    [Fact]
    public void Each_computer_watches_desktop_and_documents_until_it_is_given_its_own_folders()
    {
        var settings = new RecentFilesSettings();
        var a = Computer("A", "a");
        var b = Computer("B", "b");
        Assert.Equal(["%DESKTOP%", "%DOCUMENTS%"], RecentFilesPlan.WatchedFolders(settings, a));

        RecentFilesPlan.SetWatchedFolders(settings, a, "%DESKTOP%\r\n\"D:\\Projects\"\n  \n%desktop%\nD:\\Projects");
        Assert.Equal(["%DESKTOP%", @"D:\Projects"], RecentFilesPlan.WatchedFolders(settings, a));
        Assert.Equal(RecentFiles.DefaultFolders, RecentFilesPlan.WatchedFolders(settings, b));

        RecentFilesPlan.SetWatchedFolders(settings, a, "%DESKTOP%\n%DOCUMENTS%");
        Assert.False(settings.WatchedFolders.ContainsKey("a"));
        RecentFilesPlan.SetWatchedFolders(settings, b, "E:\\Work");
        RecentFilesPlan.SetWatchedFolders(settings, b, "   ");
        Assert.Empty(settings.WatchedFolders);
    }

    [Fact]
    public void No_more_folders_are_kept_than_a_client_looks_at() =>
        Assert.Equal(RecentFiles.MaxFolders, RecentFilesPlan.ParseFolders(string.Join("\n", Enumerable.Range(0, 30).Select(i => $@"D:\F{i}"))).Count);

    // ---- what is asked for ------------------------------------------------------------------

    [Fact]
    public void The_ticked_groups_send_their_extensions_and_folders_and_other_files_send_their_flags()
    {
        var groups = RecentFilesPlan.DefaultGroups();
        groups[0].Extensions = ["DOCX", "*.pdf", "bad name"];
        var query = RecentFilesPlan.Query(["%DESKTOP%"], groups, [FileTypes.Documents], NowUtc.AddMinutes(-30), null, 30);
        Assert.Equal([".docx", ".pdf"], query.Extensions.Order(StringComparer.Ordinal));
        Assert.False(query.AllFiles);
        Assert.False(query.IncludeFolders);
        Assert.Equal(30, query.WithinMinutes);
        Assert.Equal(["%DESKTOP%"], query.Folders);

        var all = RecentFilesPlan.Query(["%DESKTOP%"], groups, [FileTypes.Images, FileTypes.Folders, FileTypes.Other], NowUtc, NowUtc, null);
        Assert.True(all.AllFiles);
        Assert.True(all.IncludeFolders);
        Assert.Contains(".png", all.Extensions);
        Assert.DoesNotContain(".docx", all.Extensions);
        Assert.Equal(NowUtc, all.UntilUtc);
    }

    [Fact]
    public void Only_the_ticked_kinds_are_listed()
    {
        var groups = FileTypes.Defaults;
        IReadOnlyCollection<string> kinds = [FileTypes.Documents, FileTypes.Other];
        Assert.True(RecentFilesPlan.Wanted(Entry("%DESKTOP%", "essay.docx"), groups, kinds));
        Assert.True(RecentFilesPlan.Wanted(Entry("%DESKTOP%", "setup.exe"), groups, kinds));
        Assert.False(RecentFilesPlan.Wanted(Entry("%DESKTOP%", "photo.png"), groups, kinds));
        Assert.False(RecentFilesPlan.Wanted(Entry("%DESKTOP%", "Project", folder: true), groups, kinds));
        Assert.True(RecentFilesPlan.Wanted(Entry("%DESKTOP%", "Project", folder: true), groups, [FileTypes.Folders]));
        Assert.Equal(FileTypes.Folders, RecentFilesPlan.Kind(Entry("%DESKTOP%", "Project.docx", folder: true), groups));
    }

    [Fact]
    public void The_kinds_are_the_groups_then_folders_then_other_files() =>
        Assert.Equal([FileTypes.Documents, FileTypes.Images, FileTypes.SourceCode, FileTypes.Folders, FileTypes.Other],
            RecentFilesPlan.Kinds(RecentFilesPlan.DefaultGroups()));

    // ---- time windows -----------------------------------------------------------------------

    [Fact]
    public void The_last_30_minutes_is_the_default_and_counts_back_on_the_clients_clock()
    {
        Assert.Equal(RecentFilesPlan.DefaultWindow, new RecentFilesSettings().Window);
        var window = RecentFilesPlan.Windows.Single(item => item.Name == RecentFilesPlan.DefaultWindow);
        var now = new DateTime(2026, 10, 24, 15, 0, 0, DateTimeKind.Local);
        var (since, until, minutes) = RecentFilesPlan.Range(window, now, null, null);
        Assert.Equal(now.ToUniversalTime().AddMinutes(-30), since);
        Assert.Null(until);
        Assert.Equal(30, minutes);
    }

    [Fact]
    public void Today_starts_at_local_midnight()
    {
        var now = new DateTime(2026, 10, 24, 15, 20, 0, DateTimeKind.Local);
        var (since, until, minutes) = RecentFilesPlan.Range(RecentFilesPlan.Windows.Single(item => item.Name == "Today"), now, null, null);
        Assert.Equal(new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(), since);
        Assert.Null(until);
        Assert.Null(minutes);
    }

    [Fact]
    public void A_custom_window_uses_both_dates_and_ignores_an_end_before_its_start()
    {
        var custom = RecentFilesPlan.Windows.Single(item => item.Name == RecentFilesPlan.CustomWindow);
        var from = new DateTime(2026, 10, 20, 8, 0, 0);
        var to = new DateTime(2026, 10, 21, 16, 0, 0);
        var (since, until, minutes) = RecentFilesPlan.Range(custom, DateTime.Now, from, to);
        Assert.Equal(DateTime.SpecifyKind(from, DateTimeKind.Local).ToUniversalTime(), since);
        Assert.Equal(DateTime.SpecifyKind(to, DateTimeKind.Local).ToUniversalTime(), until);
        Assert.Null(minutes);
        Assert.Null(RecentFilesPlan.Range(custom, DateTime.Now, to, from).UntilUtc);
    }

    [Fact]
    public void Every_window_has_a_way_to_find_its_start()
    {
        Assert.Contains(RecentFilesPlan.Windows, item => item.Name == RecentFilesPlan.CustomWindow);
        Assert.All(RecentFilesPlan.Windows.Where(item => item.Name != RecentFilesPlan.CustomWindow),
            item => Assert.True(item.Minutes is > 0 || item.StartLocal is not null, item.Name));
        Assert.Equal(RecentFilesPlan.Windows.Count, RecentFilesPlan.Windows.Select(item => item.Name).Distinct().Count());
    }

    // ---- where copies go --------------------------------------------------------------------

    [Theory]
    [InlineData("%DESKTOP%", "Desktop")]
    [InlineData("%documents%", "Documents")]
    [InlineData("%DOWNLOADS%", "Downloads")]
    [InlineData("%USERPROFILE%", "Profile")]
    [InlineData(@"D:\Projects\", "Projects")]
    [InlineData(@"D:\", "D")]
    [InlineData("%APPDATA%", "APPDATA")]
    [InlineData("", "Folder")]
    public void Each_watched_folder_gets_a_plain_name_here(string root, string label) =>
        Assert.Equal(label, RecentFilesPlan.FolderLabel(root));

    [Fact]
    public void A_copy_goes_under_the_computer_the_student_and_the_watched_folder()
    {
        var path = RecentFilesPlan.LocalPath(Destination, Computer(user: "jonas"), Entry("%DESKTOP%", @"Project\a.py"));
        Assert.Equal(@"D:\Recent files\410-05_jonas\Desktop\Project\a.py", path);
        Assert.Equal(@"D:\Recent files\410-05\Documents\a.docx", RecentFilesPlan.LocalPath(Destination, Computer(), Entry("%DOCUMENTS%", "a.docx")));
    }

    [Fact]
    public void A_file_inside_a_copied_folder_keeps_its_place_in_that_folder() =>
        Assert.Equal(@"D:\Recent files\410-05\Desktop\Project\src\main.py",
            RecentFilesPlan.LocalPath(Destination, Computer(), Entry("%DESKTOP%", @"src\main.py"), insideFolder: "Project"));

    [Theory]
    [InlineData(@"..\..\Windows\evil.dll")]
    [InlineData(@"C:\Windows\evil.dll")]
    [InlineData("")]
    public void A_name_that_would_leave_the_destination_is_not_copied(string relative) =>
        Assert.Null(RecentFilesPlan.LocalPath(Destination, Computer(), new RecentEntry("%DESKTOP%", @"C:\x", relative, false, 1, NowUtc)));

    [Fact]
    public void A_folder_inside_that_would_leave_is_refused_too() =>
        Assert.Null(RecentFilesPlan.LocalPath(Destination, Computer(), Entry("%DESKTOP%", "a.txt"), insideFolder: @"..\..\.."));

    [Fact]
    public void Names_windows_does_not_allow_are_replaced()
    {
        Assert.Equal(@"D:\Recent files\PC_1_ana_b\Desktop\a.txt",
            RecentFilesPlan.LocalPath(Destination, Computer("PC:1", user: "ana|b"), Entry("%DESKTOP%", "a.txt")));
        Assert.Equal("Computer", RecentFilesPlan.Safe("::", "Computer"));
        Assert.Equal("name", RecentFilesPlan.Safe(" name. ", "x"));
    }

    [Fact]
    public void What_lies_inside_a_copied_folder_is_not_copied_twice()
    {
        var project = Entry("%DESKTOP%", "Project", folder: true);
        var inside = Entry("%DESKTOP%", @"Project\lib\a.py");
        var subfolder = Entry("%DESKTOP%", @"Project\lib", folder: true);
        var lookalike = Entry("%DESKTOP%", @"Project2\b.py");
        var alone = Entry("%DESKTOP%", "essay.docx");
        Assert.Equal([project, lookalike, alone], RecentFilesPlan.WithoutNested([inside, project, subfolder, lookalike, alone]));
        Assert.Equal([inside, alone], RecentFilesPlan.WithoutNested([inside, alone]));
    }

    // ---- overwrite and retention ------------------------------------------------------------

    [Fact]
    public void A_file_is_copied_when_it_is_new_and_replaced_only_when_it_changed_and_overwriting_is_on()
    {
        var entry = Entry("%DESKTOP%", "a.docx", size: 100, modified: NowUtc);
        Assert.True(RecentFilesPlan.NeedsCopy(entry, null, overwrite: false));
        Assert.True(RecentFilesPlan.NeedsCopy(entry, null, overwrite: true));
        Assert.False(RecentFilesPlan.NeedsCopy(entry, new LocalFile(100, NowUtc.AddMilliseconds(400)), overwrite: true));
        Assert.True(RecentFilesPlan.NeedsCopy(entry, new LocalFile(99, NowUtc), overwrite: true));
        Assert.True(RecentFilesPlan.NeedsCopy(entry, new LocalFile(100, NowUtc.AddSeconds(-5)), overwrite: true));
        Assert.False(RecentFilesPlan.NeedsCopy(entry, new LocalFile(99, NowUtc.AddSeconds(-5)), overwrite: false));
    }

    [Fact]
    public void Overwriting_is_on_and_copies_stay_30_days_by_default()
    {
        var settings = new RecentFilesSettings();
        Assert.True(settings.Overwrite);
        Assert.Equal(30, settings.KeepDays);
        Assert.Equal([FileTypes.Documents, FileTypes.Images, FileTypes.SourceCode], settings.CheckedKinds);
        Assert.Equal("", settings.Destination);
    }

    [Fact]
    public void Copies_expire_after_the_days_kept_and_never_when_kept_always()
    {
        var records = new[]
        {
            new RecentCopyRecord { LocalPath = "old", CopiedUtc = NowUtc.AddDays(-31) },
            new RecentCopyRecord { LocalPath = "new", CopiedUtc = NowUtc.AddDays(-29) },
        };
        Assert.Equal(["old"], RecentFilesPlan.Expired(records, NowUtc, 30).Select(record => record.LocalPath));
        Assert.Empty(RecentFilesPlan.Expired(records, NowUtc, 0));
        Assert.Equal(2, RecentFilesPlan.Expired(records, NowUtc, 1).Count);
    }

    [Fact]
    public void An_expired_copy_someone_changed_here_is_kept()
    {
        var record = new RecentCopyRecord { ModifiedUtc = NowUtc };
        Assert.True(RecentFilesPlan.MayDelete(record, NowUtc.AddMilliseconds(300)));
        Assert.False(RecentFilesPlan.MayDelete(record, NowUtc.AddMinutes(5)));
        Assert.False(RecentFilesPlan.MayDelete(record, null));
    }

    [Fact]
    public void A_file_copied_again_has_one_record_and_the_oldest_records_go_first()
    {
        var settings = new RecentFilesSettings();
        RecentFilesPlan.Remember(settings, @"D:\a.txt", NowUtc, NowUtc);
        RecentFilesPlan.Remember(settings, @"d:\A.TXT", NowUtc.AddHours(1), NowUtc.AddHours(1));
        Assert.Equal(NowUtc.AddHours(1), Assert.Single(settings.Copies).CopiedUtc);

        settings.Copies = Enumerable.Range(0, RecentFilesPlan.MaxRecords).Select(i => new RecentCopyRecord { LocalPath = $"f{i}" }).ToList();
        RecentFilesPlan.Remember(settings, "last", NowUtc, NowUtc);
        Assert.Equal(RecentFilesPlan.MaxRecords, settings.Copies.Count);
        Assert.Equal("f1", settings.Copies[0].LocalPath);
        Assert.Equal("last", settings.Copies[^1].LocalPath);
    }

    [Fact]
    public void Pruning_deletes_only_unchanged_expired_copies_inside_the_destination_and_their_empty_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "cm-recent-prune-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "cm-recent-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            string Write(string relative, DateTime modified)
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, modified);
                return path;
            }
            var expired = Write(@"PC\Desktop\Deep\old.txt", NowUtc.AddDays(-40));
            var edited = Write(@"PC\Desktop\edited.txt", NowUtc.AddDays(-1));
            var recent = Write(@"PC\Desktop\recent.txt", NowUtc.AddDays(-2));
            File.WriteAllText(outside, "not ours");
            var settings = new RecentFilesSettings
            {
                KeepDays = 30,
                Copies =
                [
                    new() { LocalPath = expired, CopiedUtc = NowUtc.AddDays(-35), ModifiedUtc = NowUtc.AddDays(-40) },
                    new() { LocalPath = edited, CopiedUtc = NowUtc.AddDays(-35), ModifiedUtc = NowUtc.AddDays(-40) },
                    new() { LocalPath = recent, CopiedUtc = NowUtc.AddDays(-2), ModifiedUtc = NowUtc.AddDays(-2) },
                    new() { LocalPath = outside, CopiedUtc = NowUtc.AddDays(-35), ModifiedUtc = File.GetLastWriteTimeUtc(outside) },
                    new() { LocalPath = Path.Combine(root, "gone.txt"), CopiedUtc = NowUtc.AddDays(-35) },
                ],
            };

            Assert.Equal(1, RecentFilesCopier.Prune(settings, root, NowUtc));

            Assert.False(File.Exists(expired));
            Assert.False(Directory.Exists(Path.GetDirectoryName(expired)));
            Assert.True(File.Exists(edited));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(outside));
            Assert.True(Directory.Exists(root));
            Assert.Equal([recent], settings.Copies.Select(record => record.LocalPath));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
            try { File.Delete(outside); } catch (IOException) { }
        }
    }

    [Fact]
    public void The_settings_survive_a_save_and_a_load()
    {
        var path = Path.Combine(HostSettings.DataFolder, "recent-files-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new HostSettings();
        RecentFilesPlan.SetWatchedFolders(settings.RecentFiles, Computer(id: "C1"), @"D:\Projects");
        settings.RecentFiles.TypeGroups[0].Extensions = [".docx"];
        settings.RecentFiles.CheckedKinds = [FileTypes.Folders];
        settings.RecentFiles.Overwrite = false;
        settings.RecentFiles.KeepDays = 7;
        settings.RecentFiles.Destination = @"E:\Copies";
        RecentFilesPlan.Remember(settings.RecentFiles, @"E:\Copies\a.txt", NowUtc, NowUtc);
        settings.Save(path);

        var loaded = HostSettings.Load(path).RecentFiles;
        Assert.Equal([@"D:\Projects"], RecentFilesPlan.WatchedFolders(loaded, Computer(id: "c1")));
        Assert.Equal([@"D:\Projects"], RecentFilesPlan.WatchedFolders(loaded, Computer(id: "C1")));
        Assert.Equal([".docx"], loaded.TypeGroups[0].Extensions);
        Assert.Equal(3, loaded.TypeGroups.Count);
        Assert.Equal([FileTypes.Folders], loaded.CheckedKinds);
        Assert.False(loaded.Overwrite);
        Assert.Equal(7, loaded.KeepDays);
        Assert.Equal(@"E:\Copies", loaded.Destination);
        Assert.Equal(NowUtc, Assert.Single(loaded.Copies).CopiedUtc);
        File.Delete(path);
    }
}
