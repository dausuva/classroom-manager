// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public class DisplayPowerRulesTests
{
    [Theory]
    [InlineData(600, 20, 1800)]     // saver after 10 minutes, shown for 20: dark after 30
    [InlineData(600, 0, 0)]         // 0 minutes means never turn the display off
    [InlineData(60, 1, 120)]
    [InlineData(600, 100000, DisplayPowerRules.MaxSeconds)]
    public void The_display_time_is_the_screen_saver_wait_plus_the_minutes_it_is_shown(int waitSeconds, int minutesShown, int expected) =>
        Assert.Equal(expected, DisplayPowerRules.AfterScreenSaver(waitSeconds, minutesShown));

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(10, DisplayPowerRules.MinSeconds)]
    [InlineData(900, 900)]
    [InlineData(int.MaxValue, DisplayPowerRules.MaxSeconds)]
    public void Seconds_are_kept_between_a_minute_and_a_day_and_zero_means_never(int requested, int expected) =>
        Assert.Equal(expected, DisplayPowerRules.Clamp(requested));

    [Fact]
    public void An_empty_field_leaves_the_setting_alone_and_zero_is_a_real_choice()
    {
        Assert.True(DisplayPowerRules.TryParseMinutesShown("  ", out var empty));
        Assert.Null(empty);
        Assert.True(DisplayPowerRules.TryParseMinutesShown("0", out var never));
        Assert.Equal(0, never);
        Assert.True(DisplayPowerRules.TryParseMinutesShown(" 45 ", out var minutes));
        Assert.Equal(45, minutes);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("ten")]
    [InlineData("1.5")]
    [InlineData("100000")]
    public void Anything_else_is_refused(string text) => Assert.False(DisplayPowerRules.TryParseMinutesShown(text, out _));

    [Theory]
    [InlineData(0, "never")]
    [InlineData(1800, "after 30 idle minute(s)")]
    [InlineData(90, "after 90 idle second(s)")]
    public void Times_are_described_in_words(int seconds, string expected) => Assert.Equal(expected, DisplayPowerRules.Describe(seconds));
}
