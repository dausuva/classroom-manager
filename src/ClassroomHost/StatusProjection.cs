// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public sealed record StatusMetadataDisplay(string Ping, string LastSeen, string Version, string AppVersion,
    string Architecture, string WindowsVersion, int? BlockedWebsiteCount, IReadOnlyList<string>? Capabilities);
public sealed record StatusClockDisplay(double OffsetSeconds, string Text);
public sealed record StatusAvailabilityDisplay(string Level, string OnlineBaseStatus, string CompatibilityWarning);
public sealed record StatusHostProtocol(int Major, int Minor, string Version);

/// <summary>Pure core presentation. English source text remains invariant beneath the existing UI translator.</summary>
public static class StatusProjection
{
    public static StatusMetadataDisplay Metadata(StatusObservation observation, DateTime localNow)
    {
        var ping = observation.Ping;
        return new($"{observation.RoundTripMilliseconds:0} ms", localNow.ToString("HH:mm:ss"), ping.Version, ClassroomManager.Shared.ReleaseVersions.Display(ping.AppVersion),
            ping.Architecture, ping.WindowsVersion, ping.BlockedWebsiteCount,
            ping.Capabilities is null ? null : Array.AsReadOnly(ping.Capabilities.ToArray()));
    }

    public static string InterfaceLanguage(bool supported, bool helper, string installed, string? userLanguage = null)
    {
        if (!supported) return installed.Length == 0 ? "" : installed + " (system)";
        if (!helper) return "(no signed-in user)";
        return string.IsNullOrEmpty(userLanguage) ? "(unavailable)" : userLanguage;
    }

    public static string OpenWindows(StatusWindows? windows) => windows is null ? "" : $"{windows.Applications} / {windows.Windows}";

    public static string Uptime(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}m";
    }

    public static StatusClockDisplay? Clock(StatusObservation observation)
    {
        if (observation.Ping.UtcTicks is not { } ticks) return null;
        var clientUtc = new DateTime(ticks, DateTimeKind.Utc);
        var offset = (clientUtc - observation.SentUtc.AddMilliseconds(observation.RoundTripMilliseconds / 2)).TotalSeconds;
        return new(offset, Offset(offset) + (Math.Abs(offset) > 5 ? "  !" : ""));
    }

    public static string Offset(double seconds)
    {
        var sign = seconds < 0 ? "-" : "+";
        var a = Math.Abs(seconds);
        if (a < 1) return $"{sign}{a * 1000:0} ms";
        if (a < 120) return $"{sign}{a:0.0} s";
        if (a < 7200) return $"{sign}{a / 60:0.0} min";
        return $"{sign}{a / 3600:0.0} h";
    }

    public static StatusAvailabilityDisplay Availability(bool helper, string clientVersion, StatusHostProtocol host)
    {
        var warning = CompatibilityWarning(clientVersion, host);
        return new(warning.Length > 0 ? "warn" : helper ? "ok" : "idle", helper ? "Online" : "Online - nobody signed in", warning);
    }

    static string CompatibilityWarning(string clientVersion, StatusHostProtocol host)
    {
        if (!Protocol.TryParseVersion(clientVersion, out var major, out var minor))
            return clientVersion.Length == 0 ? "" : $" - unrecognized client protocol {clientVersion}";
        if (major != host.Major)
            return major < host.Major
                ? $" - incompatible client protocol v{clientVersion}; update client"
                : $" - client protocol v{clientVersion} requires a newer Host";
        if (minor < host.Minor)
            return $" - some features unavailable (client protocol v{clientVersion}; update client)";
        if (minor > host.Minor)
            return $" - client protocol v{clientVersion} is newer than Host v{host.Version}; update Host";
        return "";
    }
}
