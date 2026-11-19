// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Host;
using System.Globalization;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class QuickActionTests
{
    [Fact]
    public void Normalize_keeps_known_actions_once_and_drops_unknown_actions()
    {
        var actions = QuickActionCatalog.Normalize(["lock", "unknown", "LOCK", "wake"]);

        Assert.Equal(["lock"], actions);
    }

    [Fact]
    public void Normalize_defaults_to_lock_and_unlock_when_the_setting_is_missing()
    {
        Assert.Equal(["lock", "unlock", "send-files"], QuickActionCatalog.Normalize(null));
    }

    [Fact]
    public void Every_quick_action_has_an_icon_and_accessible_label()
    {
        Assert.All(QuickActionCatalog.All, action => Assert.False(string.IsNullOrWhiteSpace(action.Icon)));
        Assert.All(QuickActionCatalog.All, action => Assert.InRange(action.Icon[0], '\uE000', '\uF8FF'));
        Assert.All(QuickActionCatalog.All, action => Assert.False(string.IsNullOrWhiteSpace(action.Label)));
    }

    [Fact]
    public void Legacy_symbols_are_presented_as_fluent_glyphs()
    {
        var converter = new FluentIconConverter();

        var icon = Assert.IsType<string>(converter.Convert("🔒", typeof(string), new object(), CultureInfo.InvariantCulture));

        Assert.Equal(FluentIcons.Lock, icon);
    }

    [Theory]
    [InlineData("\U0001F5A5", FluentIcons.Display)] // Interface settings
    [InlineData("\U0001F4C5", FluentIcons.Calendar)] // Schedules
    [InlineData("ⓘ", FluentIcons.Info)] // About
    public void Settings_toolbar_symbols_are_presented_as_fluent_glyphs(string symbol, string expected)
    {
        var icon = new FluentIconConverter().Convert(symbol, typeof(string), new object(), CultureInfo.InvariantCulture);

        Assert.Equal(expected, icon);
    }

    [Fact]
    public void Exam_mode_can_be_selected_as_a_quick_action()
    {
        Assert.Contains(QuickActionCatalog.All, action => action.Id == "exam");
        Assert.Equal(["exam"], QuickActionCatalog.Normalize(["exam"]));
    }
}
