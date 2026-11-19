// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>Core presentation adapter; caller supplies dispatcher verification and the live roster. No transport or persistence.</summary>
public sealed class StatusPresentationTarget(StatusRefreshTarget target, IReadOnlyCollection<ClientInfo> roster, Action verifyAccess)
{
    bool CanApply()
    {
        verifyAccess();
        return target.Matches() && roster.Contains(target.Client);
    }

    public bool ApplyMetadata(StatusMetadataDisplay display)
    {
        if (!CanApply()) return false;
        var client = target.Client;
        client.Ping = display.Ping;
        client.Unreachable = false;
        client.LastSeen = display.LastSeen;
        client.Version = display.Version;
        client.AppVersion = display.AppVersion;
        client.Architecture = display.Architecture;
        client.WindowsVersion = display.WindowsVersion;
        client.BlockedWebsiteCount = display.BlockedWebsiteCount;
        client.Capabilities.Clear();
        client.CapabilitiesAdvertised = display.Capabilities is not null;
        foreach (var capability in display.Capabilities ?? []) client.Capabilities.Add(capability);
        return true;
    }

    public bool ApplyLanguage(string language)
    {
        if (!CanApply()) return false;
        target.Client.InterfaceLanguage = language;
        return true;
    }

    public bool ApplyWindows(string windows)
    {
        if (!CanApply()) return false;
        target.Client.OpenWindows = windows;
        return true;
    }

    public bool ApplyOnline(StatusObservation observation, StatusHostProtocol host)
    {
        if (!CanApply()) return false;
        var client = target.Client;
        // Missing legacy timing fields deliberately retain the previous value, unlike a failed connection's clearing policy.
        if (observation.Ping.UptimeSeconds is { } uptime) client.Uptime = StatusProjection.Uptime(uptime);
        if (StatusProjection.Clock(observation) is { } clock)
        {
            client.ClockOffsetSec = clock.OffsetSeconds;
            client.Clock = clock.Text;
        }
        var online = StatusProjection.Availability(observation.Ping.UserAgent, client.Version, host);
        client.Level = online.Level;
        client.OnlineBaseStatus = online.OnlineBaseStatus;
        client.CompatibilityWarning = online.CompatibilityWarning;
        client.Status = client.CompatibleOnlineStatus;
        return true;
    }
}
