// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;

namespace ClassroomManager.Host;

public sealed record ConnectedAccountInventory(
    string Computer,
    string State,
    string User,
    string Identity,
    bool? MicrosoftAccountConnected,
    IReadOnlyList<string> CredentialTargets,
    string Error)
{
    public string MicrosoftAccountText => MicrosoftAccountConnected switch
    {
        true => "Detected",
        false => "Not detected",
        _ => "Unknown",
    };

    public string CredentialTargetsText => CredentialTargets.Count == 0
        ? (State == "Available" ? "None reported" : "Unavailable")
        : string.Join("\n", CredentialTargets);

    public static ConnectedAccountInventory FromResponse(string computer, JsonObject data)
    {
        var targets = data["credentials"]?.AsArray()
            .Select(node => node?["target"]?.GetValue<string>() ?? "")
            .Where(target => target.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(target => target, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        return new ConnectedAccountInventory(
            computer,
            "Available",
            data["user"]?.GetValue<string>() ?? "",
            data["identity"]?.GetValue<string>() ?? "",
            data["microsoftAccountConnected"]?.GetValue<bool>(),
            targets,
            "");
    }

    public static ConnectedAccountInventory Unavailable(string computer, string error, bool unsupported = false)
    {
        var state = unsupported
            ? "Unsupported"
            : error.Contains("No user is signed in", StringComparison.OrdinalIgnoreCase)
                ? "No signed-in user"
                : "Unavailable";
        return new ConnectedAccountInventory(computer, state, "", "", null, [], error);
    }
}
