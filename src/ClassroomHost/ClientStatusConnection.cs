// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Non-owning read adapter. The core caller retains the authenticated session for its existing recovery sequence.</summary>
public sealed class ClientStatusConnection(ClientSession session) : IStatusConnection
{
    public async Task<StatusPing> PingAsync(CancellationToken cancellation) => CapturePing(await session.CallAsync(Cmd.Ping, ct: cancellation));

    public async Task<string?> InterfaceLanguageAsync(CancellationToken cancellation) =>
        (await session.CallAtAsync(Levels.User, Cmd.WindowsPreferencesGet, ct: cancellation))["interfaceLanguage"]?.GetValue<string>();

    public async Task<StatusWindows> OpenWindowsAsync(CancellationToken cancellation)
    {
        var data = await session.CallAtAsync(Levels.User, Cmd.ProcWindows, ct: cancellation);
        return new(data["appCount"]?.GetValue<int>() ?? 0, data["windowCount"]?.GetValue<int>() ?? 0);
    }

    public static StatusPing CapturePing(JsonObject info) => new()
    {
        MachineName = info["machineName"]?.GetValue<string>(),
        UserName = info["userName"]?.GetValue<string>() ?? "",
        ClientId = info["clientId"]?.GetValue<string>()?.Trim() ?? "",
        Mac = info["mac"]?.GetValue<string>() ?? "",
        Version = info["version"]?.GetValue<string>() ?? "",
        AppVersion = info["appVersion"]?.GetValue<string>() ?? "",
        Architecture = info["architecture"]?.GetValue<string>() ?? "",
        WindowsVersion = info["windowsVersion"]?.GetValue<string>() ?? "",
        BlockedWebsiteCount = info["blockedWebsiteCount"]?.GetValue<int>(),
        Capabilities = info["capabilities"] is JsonArray capabilities ? Array.AsReadOnly(capabilities
            .Select(capability => capability?.GetValue<string>()).Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).ToArray()) : null,
        UserAgent = info["userAgent"]?.GetValue<bool>() ?? false,
        InterfaceLanguage = info["interfaceLanguage"]?.GetValue<string>() ?? "",
        UptimeSeconds = info["uptimeSec"]?.GetValue<long>(),
        UtcTicks = info["utcTicks"]?.GetValue<long>(),
        Role = info["role"]?.GetValue<string>() ?? "",
        DesktopRuntime = info["desktopRuntime"]?.GetValue<string>(),
    };
}
