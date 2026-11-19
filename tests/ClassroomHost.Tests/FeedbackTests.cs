// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using System.IO;
using System.Windows.Input;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class FeedbackTests
{
    static readonly System.Windows.Rect Screen = new(0, 0, 1920, 1080);
    static readonly System.Windows.Size FeedbackWindow = new(480, 430);

    [Fact]
    public void Feedback_window_opens_in_the_bottom_right_corner_of_the_described_window()
    {
        var position = FeedbackPlacement.BottomRight(new System.Windows.Rect(100, 50, 1300, 800), FeedbackWindow, Screen);

        Assert.Equal(new System.Windows.Point(896, 396), position);
    }

    [Fact]
    public void Feedback_window_stays_on_screen_when_the_described_window_is_small_or_partly_off_screen()
    {
        var smallDialogNearOrigin = new System.Windows.Rect(20, 30, 360, 200);
        var hangingOffBottomRight = new System.Windows.Rect(1500, 700, 900, 700);

        Assert.Equal(new System.Windows.Point(0, 0), FeedbackPlacement.BottomRight(smallDialogNearOrigin, FeedbackWindow, Screen));
        Assert.Equal(new System.Windows.Point(1440, 650), FeedbackPlacement.BottomRight(hangingOffBottomRight, FeedbackWindow, Screen));
    }

    [Fact]
    public void Feedback_window_respects_a_virtual_screen_that_starts_left_of_the_primary_monitor()
    {
        var onLeftMonitor = new System.Windows.Rect(-1900, 10, 400, 300);

        Assert.Equal(new System.Windows.Point(-1920, 0),
            FeedbackPlacement.BottomRight(onLeftMonitor, FeedbackWindow, new System.Windows.Rect(-1920, 0, 3840, 1080)));
    }

    [Fact]
    public void Store_round_trips_and_cleans_only_shared_entries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ClassroomHost.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "feedback.json");
        try
        {
            var store = new FeedbackStore(path);
            var saved = Entry("saved", FeedbackStatuses.Saved);
            var shared = Entry("shared", FeedbackStatuses.ShareOpened);
            store.Upsert(saved);
            store.Upsert(shared);

            var loaded = store.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, x => x.Id == saved.Id && x.Text == "saved");

            Assert.Equal(1, store.RemoveShared());
            var remaining = Assert.Single(store.Load());
            Assert.Equal(saved.Id, remaining.Id);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Report_contains_support_version_context_and_feedback_id()
    {
        var entry = Entry("The refresh button did not update the list.", FeedbackStatuses.Saved);
        var report = FeedbackReport.Format(new[] { entry },
            new ProductSupportSettings { SupportEmail = "quality@example.test" },
            new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc));

        Assert.Contains("Support: quality@example.test", report);
        Assert.Contains("Host version:", report);
        Assert.Contains($"Feedback ID: {entry.Id}", report);
        Assert.Contains("Context ID: MainWindow/computers", report);
        Assert.Contains("Visible tab: Computers", report);
        Assert.Contains(entry.Text, report);
        Assert.DoesNotContain("Status:", report);
        Assert.DoesNotContain("Updated UTC:", report);
    }

    [Fact]
    public void Product_support_address_comes_from_embedded_settings()
    {
        Assert.Equal("support@dausuva.com", ProductSupportSettings.Load().SupportEmail);
    }

    [Fact]
    public void Main_window_title_contains_the_brand_and_version()
    {
        Assert.Equal("Dausuva Classroom Manager 1.2.0.119", HostWindowTitle.Format("1.2.0.119"));
    }

    [Fact]
    public void Informational_and_feedback_windows_do_not_get_nested_feedback_buttons()
    {
        Assert.True(FeedbackUi.ShouldAttachTo(typeof(MainWindow)));
        Assert.False(FeedbackUi.ShouldAttachTo(typeof(AboutWindow)));
        Assert.False(FeedbackUi.ShouldAttachTo(typeof(OpenSourceLicensesWindow)));
        Assert.False(FeedbackUi.ShouldAttachTo(typeof(FeedbackManagerWindow)));
        Assert.True(FeedbackUi.ShouldAttachTo(typeof(FormDialog)));
    }

    [Fact]
    public void Contextual_feedback_shortcut_requires_control_shift_f()
    {
        Assert.True(FeedbackUi.IsFeedbackShortcut(Key.F, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.False(FeedbackUi.IsFeedbackShortcut(Key.F, ModifierKeys.Control));
        Assert.False(FeedbackUi.IsFeedbackShortcut(Key.F1, ModifierKeys.Control | ModifierKeys.Shift));
    }

    static FeedbackEntry Entry(string text, string status) => new()
    {
        Status = status,
        ContextId = "MainWindow/computers",
        WindowTitle = "Classroom Manager",
        WindowType = "MainWindow",
        TabTitle = "Computers",
        HostVersion = "1.2.0.0",
        Text = text,
    };
}
