// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

// T-222: pure status projection preserves source text, typed values and legacy absence semantics.
public sealed class StatusProjectionTests
{
    static readonly DateTime Sent = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    static readonly StatusHostProtocol Host = new(1, 20, "1.20");

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("values")]
    public void Metadata_copies_capabilities_and_never_translates_data(string kind)
    {
        var capabilities = new List<string>();
        if (kind == "values") capabilities.Add("Close");
        var ping = new StatusPing { Version = "1.20", AppVersion = "1.2.0.42", WindowsVersion = "Close", Architecture = "x86",
            BlockedWebsiteCount = 0, Capabilities = kind == "missing" ? null : capabilities };
        var display = StatusProjection.Metadata(new(ping, Sent, 125), new DateTime(2026, 10, 27, 15, 4, 5));
        Assert.Equal("125 ms", display.Ping);
        Assert.Equal("15:04:05", display.LastSeen);
        Assert.Equal("1.20", display.Version);
        Assert.Equal("1.2.0.42", display.AppVersion);
        Assert.Equal("Close", display.WindowsVersion);
        Assert.Equal("x86", display.Architecture);
        Assert.Equal(0, display.BlockedWebsiteCount);
        if (kind == "missing") Assert.Null(display.Capabilities);
        else
        {
            Assert.Equal(capabilities, display.Capabilities);
            Assert.Throws<NotSupportedException>(() => ((IList<string>)display.Capabilities!).Add("mutation"));
            capabilities.Clear();
            Assert.Equal(kind == "values" ? 1 : 0, display.Capabilities!.Count);
        }
        Assert.Null(StatusProjection.Metadata(new(new(), Sent, 0), Sent).BlockedWebsiteCount);
    }

    [Theory]
    [InlineData(false, false, "", null, "")]
    [InlineData(false, true, "lt-LT", null, "lt-LT (system)")]
    [InlineData(true, false, "lt-LT", "en-US", "(no signed-in user)")]
    [InlineData(true, true, "lt-LT", null, "(unavailable)")]
    [InlineData(true, true, "lt-LT", "", "(unavailable)")]
    [InlineData(true, true, "lt-LT", "en-US", "en-US")]
    [InlineData(true, true, "lt-LT", "Close", "Close")]
    public void Language_preserves_user_system_and_unknown_distinctions(bool supported, bool helper, string installed, string? read, string expected) =>
        Assert.Equal(expected, StatusProjection.InterfaceLanguage(supported, helper, installed, read));

    [Fact]
    public void Unavailable_windows_are_not_a_successful_zero_count()
    {
        Assert.Equal("", StatusProjection.OpenWindows(null));
        Assert.Equal("0 / 0", StatusProjection.OpenWindows(new(0, 0)));
        Assert.Equal("2 / 4", StatusProjection.OpenWindows(new(2, 4)));
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(59, "0m")]
    [InlineData(60, "1m")]
    [InlineData(3599, "59m")]
    [InlineData(3600, "1h 0m")]
    [InlineData(86399, "23h 59m")]
    [InlineData(86400, "1d 0h")]
    [InlineData(90061, "1d 1h")]
    [InlineData(-60, "-1m")]
    public void Uptime_keeps_the_existing_grid_format(long seconds, string expected) => Assert.Equal(expected, StatusProjection.Uptime(seconds));

    [Theory]
    [InlineData(0, "+0 ms")]
    [InlineData(-0.5, "-500 ms")]
    [InlineData(0.999, "+999 ms")]
    [InlineData(1, "+1.0 s")]
    [InlineData(119.9, "+119.9 s")]
    [InlineData(120, "+2.0 min")]
    [InlineData(-120, "-2.0 min")]
    [InlineData(7199, "+120.0 min")]
    [InlineData(7200, "+2.0 h")]
    public void Offset_units_sign_and_rounding_keep_existing_boundaries(double seconds, string expected)
    {
        var before = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US"); Assert.Equal(expected, StatusProjection.Offset(seconds)); }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(-5, false)]
    [InlineData(5.001, true)]
    [InlineData(-5.001, true)]
    public void Clock_uses_round_trip_midpoint_and_a_strict_five_second_warning(double offset, bool warn)
    {
        var ping = new StatusPing { UtcTicks = Sent.AddMilliseconds(100).AddSeconds(offset).Ticks };
        var clock = StatusProjection.Clock(new(ping, Sent, 200));
        Assert.NotNull(clock);
        Assert.Equal(offset, clock.OffsetSeconds, 6);
        Assert.Equal(warn, clock.Text.EndsWith("  !", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_clock_is_absent_but_invalid_ticks_still_fail()
    {
        Assert.Null(StatusProjection.Clock(new(new(), Sent, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => StatusProjection.Clock(new(new() { UtcTicks = -1 }, Sent, 0)));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("1.20", "")]
    [InlineData("1.20.42", "")]
    [InlineData("invalid", " - unrecognized client protocol invalid")]
    [InlineData("0.20", " - incompatible client protocol v0.20; update client")]
    [InlineData("2.0", " - client protocol v2.0 requires a newer Host")]
    [InlineData("1.19", " - some features unavailable (client protocol v1.19; update client)")]
    [InlineData("1.21", " - client protocol v1.21 is newer than Host v1.20; update Host")]
    public void Compatibility_uses_the_existing_protocol_parser_and_warning_text(string version, string warning)
    {
        foreach (var helper in new[] { false, true })
        {
            var display = StatusProjection.Availability(helper, version, Host);
            Assert.Equal(warning, display.CompatibilityWarning);
            Assert.Equal(warning.Length > 0 ? "warn" : helper ? "ok" : "idle", display.Level);
            Assert.Equal(helper ? "Online" : "Online - nobody signed in", display.OnlineBaseStatus);
        }
    }

    [Theory]
    [InlineData("en-US", "+1.2 s")]
    [InlineData("lt-LT", "+1,2 s")]
    public void Culture_formats_numbers_while_core_translation_remains_a_display_step(string culture, string expected)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, StatusProjection.Offset(1.24));
            var display = StatusProjection.Availability(true, "1.20", Host);
            Assert.Equal("Online", display.OnlineBaseStatus);
            Assert.Equal(culture == "lt-LT" ? "Prisijungęs" : "Online", Loc.BuiltIn(culture).Translate(display.OnlineBaseStatus));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
}
