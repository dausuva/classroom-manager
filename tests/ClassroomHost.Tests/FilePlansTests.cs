// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>What the Send files, Collect files and folder-sync dialogs show and decide, and the Exam mode confirmation (T-253 phase 2), without a window.</summary>
public sealed class FilePlansTests
{
    // ---- Send files -------------------------------------------------------------------------

    [Fact]
    public void The_summary_of_a_few_files_lists_each_with_its_size_and_adds_up_the_bytes()
    {
        var plan = FileSendPlan.Build([("a.docx", 512), ("b.pdf", 2048), ("c.zip", 3 * 1048576)], @"C:\Users\s\Desktop", overwrite: true);
        Assert.Equal(3, plan.Count);
        Assert.Equal(512 + 2048 + 3 * 1048576L, plan.TotalBytes);
        Assert.Equal("• a.docx (512 B)\n• b.pdf (2.0 KB)\n• c.zip (3.0 MB)", plan.Preview);
        Assert.Equal("Send 3 file(s) to C:\\Users\\s\\Desktop", plan.Title);
    }

    [Fact]
    public void A_long_list_is_cut_after_twelve_with_a_dots_line_and_twelve_is_not_cut()
    {
        var twelve = Enumerable.Range(1, 12).Select(i => ($"f{i}.txt", 1L)).ToList();
        Assert.DoesNotContain("...", FileSendPlan.Build(twelve, "D", true).Preview);
        Assert.Equal(12, FileSendPlan.Build(twelve, "D", true).Preview.Split('\n').Length);
        var thirteen = Enumerable.Range(1, 13).Select(i => ($"f{i}.txt", 1L)).ToList();
        var preview = FileSendPlan.Build(thirteen, "D", true).Preview;
        Assert.EndsWith("\n• ...", preview);
        Assert.DoesNotContain("f13.txt", preview);
        Assert.Equal(13, preview.Split('\n').Length);
    }

    [Fact]
    public void The_review_says_what_goes_where_and_whether_files_are_replaced()
    {
        var overwrite = FileSendPlan.Build([("a.txt", 5)], @"D:\Work", overwrite: true);
        Assert.Equal(
        [
            ("Files", "1, 5 B\n• a.txt (5 B)"), ("Destination", @"D:\Work"), ("Existing files", "Overwrite files with the same name"),
        ], overwrite.ReviewItems.Select(item => (item.Label, item.Value)));
        Assert.Contains("can be replaced", overwrite.Warning);
        var keep = FileSendPlan.Build([("a.txt", 5)], @"D:\Work", overwrite: false);
        Assert.Equal("Keep existing files", keep.ReviewItems[2].Value);
        Assert.Equal("", keep.Warning);
    }

    [Fact]
    public void The_transfer_history_is_written_by_computer_then_file_name_in_any_case()
    {
        var records = new[]
        {
            new FileTransferRecord { Computer = "pc-b", FileName = "z.txt" }, new FileTransferRecord { Computer = "PC-A", FileName = "b.txt" },
            new FileTransferRecord { Computer = "pc-a", FileName = "A.txt" }, new FileTransferRecord { Computer = "PC-B", FileName = "a.txt" },
        };
        Assert.Equal([("pc-a", "A.txt"), ("PC-A", "b.txt"), ("PC-B", "a.txt"), ("pc-b", "z.txt")], FileSendPlan.HistoryOrder(records).Select(r => (r.Computer, r.FileName)));
    }

    // ---- Collect files ----------------------------------------------------------------------

    [Fact]
    public void A_collect_folder_must_be_a_full_path_and_the_dated_subfolder_is_named_by_the_minute()
    {
        var now = new DateTime(2026, 11, 3, 9, 5, 59);
        var plan = CollectPlan.Build(@"D:\Collected", datedSubfolder: true, now, "%DOCUMENTS%", "*.docx", recursive: true, overwrite: false);
        Assert.Null(plan.Problem);
        Assert.Equal(@"D:\Collected", plan.ChosenFolder);
        Assert.Equal(Path.Combine(@"D:\Collected", "2026-11-03_0905"), plan.LocalRoot);
        Assert.Equal(("%DOCUMENTS%", "*.docx", true, false), (plan.StudentFolder, plan.Pattern, plan.Recursive, plan.Overwrite));
        Assert.Equal(@"D:\Collected", CollectPlan.Build(@"D:\Collected", false, now, "x", "*", false, true).LocalRoot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Collected")]
    [InlineData(@"..\Collected")]
    public void A_folder_that_is_not_a_full_path_is_refused(string local)
    {
        var plan = CollectPlan.Build(local, true, DateTime.Now, "x", "*", false, true);
        Assert.Equal("Choose a folder to save into.", plan.Problem);
    }

    [Fact]
    public void An_empty_pattern_means_every_file()
    {
        Assert.Equal("*.*", CollectPlan.Build(@"D:\C", false, DateTime.Now, "x", "", false, true).Pattern);
        Assert.Equal("homework*.*", CollectPlan.Build(@"D:\C", false, DateTime.Now, "x", "homework*.*", false, true).Pattern);
    }

    // ---- folder sync ------------------------------------------------------------------------

    [Fact]
    public void The_sync_pattern_is_the_action_argument_or_every_file()
    {
        Assert.Equal("*.*", FolderSyncPlan.Pattern(new CustomQuickAction { Arguments = "" }));
        Assert.Equal("*.docx", FolderSyncPlan.Pattern(new CustomQuickAction { Arguments = "*.docx" }));
    }

    [Fact]
    public void A_computers_file_list_is_read_with_paths_relative_to_the_folder_and_unknown_sizes_as_minus_one()
    {
        var found = new JsonObject
        {
            ["folder"] = @"C:\Users\s\Documents",
            ["files"] = new JsonArray(
                new JsonObject { ["path"] = @"C:\Users\s\Documents\a.docx", ["size"] = 10L, ["modified"] = "2026-10-14T09:15:02" },
                new JsonObject { ["path"] = @"C:\Users\s\Documents\sub\b.txt", ["modified"] = "not a time" }),
        };
        var (folder, files) = FolderSyncPlan.ReadFound(found);
        Assert.Equal(@"C:\Users\s\Documents", folder);
        Assert.Equal(2, files.Count);
        Assert.Equal(("a.docx", 10L, new DateTime(2026, 10, 14, 9, 15, 2)), (files[0].RelativePath, files[0].Size, files[0].Modified));
        Assert.Equal((@"sub\b.txt", -1L, DateTime.MinValue), (files[1].RelativePath, files[1].Size, files[1].Modified));
    }

    [Fact]
    public void The_sync_status_counts_copies_and_failures()
    {
        Assert.Equal("Synced: 3 of 5 file(s) copied", FolderSyncPlan.Status(3, 5, 0));
        Assert.Equal("Synced: 3 of 5 file(s) copied, 2 could not be copied", FolderSyncPlan.Status(3, 5, 2));
    }

    // ---- Exam mode confirmation -------------------------------------------------------------

    [Fact]
    public void Switching_on_with_nothing_set_up_is_refused_but_switching_off_never_is()
    {
        Assert.True(ExamModeChangePrompt.NothingToApply(true, 0, 0, ""));
        Assert.False(ExamModeChangePrompt.NothingToApply(true, 1, 0, ""));
        Assert.False(ExamModeChangePrompt.NothingToApply(true, 0, 1, ""));
        Assert.False(ExamModeChangePrompt.NothingToApply(true, 0, 0, "microphone muted"));
        Assert.False(ExamModeChangePrompt.NothingToApply(false, 0, 0, ""));
    }

    [Fact]
    public void The_confirmation_names_the_number_of_computers_and_what_will_be_added_or_removed()
    {
        var on = ExamModeChangePrompt.Message(true, 24, 5, 3, "");
        Assert.Equal("Enable Exam mode on 24 visible assigned computer(s)?\n\nThis will add 5 website restriction(s) and 3 app restriction(s). Existing normal block lists remain separate.", on);
        Assert.EndsWith("\n\nDevice controls for the whole computer: camera off, USB storage blocked.",
            ExamModeChangePrompt.Message(true, 1, 0, 0, "camera off, USB storage blocked"));
        Assert.Equal("Disable Exam mode on the 24 computer(s) it was turned on for?\n\nThis removes only the Exam mode overlay, including its device controls; normal website and app block lists remain.",
            ExamModeChangePrompt.Message(false, 24, 5, 3, "camera off"));
        Assert.Equal("Confirm enable Exam mode", ExamModeChangePrompt.Caption(true));
        Assert.Equal("Confirm disable Exam mode", ExamModeChangePrompt.Caption(false));
        Assert.Contains("Exam mode settings", ExamModeChangePrompt.NothingConfigured);
        Assert.Contains("room and network filter", ExamModeChangePrompt.NoComputers);
    }
}
