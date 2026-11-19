// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class WallpaperTargetsTests
{
    [Theory]
    [InlineData(WallpaperTargets.DesktopLabel, WallpaperTarget.Desktop, true, false)]
    [InlineData(WallpaperTargets.SignInLabel, WallpaperTarget.SignInScreen, false, true)]
    [InlineData(WallpaperTargets.BothLabel, WallpaperTarget.Both, true, true)]
    [InlineData(null, WallpaperTarget.Desktop, true, false)]
    [InlineData("something else", WallpaperTarget.Desktop, true, false)]
    public void Picker_label_selects_the_places_to_change(string? label, WallpaperTarget target, bool desktop, bool signIn)
    {
        Assert.Equal(target, WallpaperTargets.FromLabel(label));
        Assert.Equal(desktop, target.IncludesDesktop());
        Assert.Equal(signIn, target.IncludesSignIn());
    }

    [Fact]
    public void Every_picker_label_maps_to_a_distinct_target()
    {
        Assert.Equal(3, WallpaperTargets.Labels.Select(WallpaperTargets.FromLabel).Distinct().Count());
        Assert.Equal(WallpaperTarget.Desktop, WallpaperTargets.FromLabel(WallpaperTargets.Labels[0]));
    }

    [Theory]
    [InlineData(WallpaperTarget.Desktop, false, "Set wallpaper")]
    [InlineData(WallpaperTarget.SignInScreen, false, "Set sign-in screen image")]
    [InlineData(WallpaperTarget.Both, false, "Set wallpaper and sign-in screen image")]
    [InlineData(WallpaperTarget.Desktop, true, "Set Windows default wallpaper")]
    [InlineData(WallpaperTarget.SignInScreen, true, "Restore Windows sign-in screen image")]
    [InlineData(WallpaperTarget.Both, true, "Restore Windows default wallpaper and sign-in screen image")]
    public void Operation_title_names_what_is_changed(WallpaperTarget target, bool windowsDefault, string title)
    {
        Assert.Equal(title, WallpaperTargets.OperationTitle(target, windowsDefault));
    }

    [Fact]
    public void A_computer_succeeds_only_when_every_requested_place_succeeded()
    {
        Assert.Equal("", WallpaperTargets.FailureMessage(WallpaperTarget.Both, null, null));
        Assert.Equal("", WallpaperTargets.FailureMessage(WallpaperTarget.Desktop, null, null));
        Assert.Equal("", WallpaperTargets.FailureMessage(WallpaperTarget.SignInScreen, null, null));
    }

    [Fact]
    public void A_partial_failure_says_which_place_failed_and_which_was_done()
    {
        Assert.Equal("Desktop wallpaper: No user is signed in; Sign-in screen: done",
            WallpaperTargets.FailureMessage(WallpaperTarget.Both, "No user is signed in", null));
        Assert.Equal("Desktop wallpaper: done; Sign-in screen: Windows Home does not support it",
            WallpaperTargets.FailureMessage(WallpaperTarget.Both, null, "Windows Home does not support it"));
        Assert.Equal("Sign-in screen: Group Policy",
            WallpaperTargets.FailureMessage(WallpaperTarget.SignInScreen, null, "Group Policy"));
        Assert.Equal("Desktop wallpaper: offline",
            WallpaperTargets.FailureMessage(WallpaperTarget.Desktop, "offline", null));
    }
}
