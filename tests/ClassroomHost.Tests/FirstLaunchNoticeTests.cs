// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>The welcome notice of a new Host (T-246): who gets it, what it says, and that a packaged Host does not look for updates.</summary>
public sealed class FirstLaunchNoticeTests
{
    [Fact]
    public void A_new_Host_with_no_key_and_no_computers_is_shown_the_notice_once()
    {
        var settings = new HostSettings();
        Assert.True(FirstLaunchNotice.IsDue(settings, 0));
        settings.FirstLaunchNoticeShown = true;
        Assert.False(FirstLaunchNotice.IsDue(settings, 0));
    }

    [Fact]
    public void A_Host_that_already_has_a_key_or_computers_is_never_interrupted_by_an_update()
    {
        Assert.False(FirstLaunchNotice.IsDue(new HostSettings { Psk = "a-key-the-teacher-typed" }, 0));
        Assert.False(FirstLaunchNotice.IsDue(new HostSettings(), 3));
    }

    [Fact]
    public void The_shown_flag_survives_saving_and_loading()
    {
        var folder = Path.Combine(TestDataFolder.Scratch, "first-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "host.json");
            new HostSettings { FirstLaunchNoticeShown = true }.Save(path);
            Assert.True(HostSettings.Load(path).FirstLaunchNoticeShown);
            new HostSettings().Save(path);
            Assert.False(HostSettings.Load(path).FirstLaunchNoticeShown);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void The_text_names_the_data_folder_the_separate_Client_and_the_absence_of_screen_capture_and_a_packaged_Host_hears_about_updates_from_Windows()
    {
        var plain = string.Join("\n", FirstLaunchNotice.Paragraphs(@"C:\Users\x\AppData\Roaming\ClassroomManager", packaged: false));
        Assert.Contains(@"C:\Users\x\AppData\Roaming\ClassroomManager", plain);
        Assert.Contains("separate Classroom Manager Client", plain);
        Assert.Contains("does not look at, capture or record", plain);
        Assert.DoesNotContain("Windows installs them", plain);
        Assert.Contains("Windows installs them", string.Join("\n", FirstLaunchNotice.Paragraphs("x", packaged: true)));
    }

    [Fact]
    public void A_packaged_Host_is_not_offered_the_update_check_and_the_privacy_page_is_on_the_product_site()
    {
        Assert.False(FirstLaunchNotice.OffersUpdateCheck(packaged: true));
        Assert.True(FirstLaunchNotice.OffersUpdateCheck(packaged: false));
        Assert.StartsWith("https://dausuva.com/school/", FirstLaunchNotice.PrivacyPage);
    }
}
