// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using System.Windows;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// Shows the Windows version of the selected computers and sets or removes the version they move
/// to (see <see cref="WindowsRelease"/>). Used by both Windows Update windows.
/// </summary>
static class WindowsVersionDialog
{
    const string Caption = "Windows version";
    const string Move = "Move to the version below and stay on it";
    const string Remove = "Remove the target (Windows offers new versions in Settings again)";

    /// <summary>Returns true when a target was set or removed on at least one computer.</summary>
    public static async Task<bool> ShowAsync(Window owner, IReadOnlyList<ClientInfo> clients, HostSettings settings)
    {
        var unsupported = clients.Where(client => !client.SupportsCommand(Cmd.WindowsUpdateTargetSet)).ToList();
        if (unsupported.Count == clients.Count)
        {
            MessageBox.Show(owner, "Showing and setting the Windows version needs an updated Client on these computers. Use Update client first.",
                Caption, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var targets = clients.Except(unsupported).ToList();

        var newest = "";
        var dlg = new FormDialog(Caption, owner)
            .Target(targets)
            .LoadAsync($"Reading the Windows version of {targets.Count} computer(s)...", async form =>
            {
                var answers = await Task.WhenAll(targets.Select(async client =>
                {
                    try
                    {
                        using var session = await ClientSession.OpenAsync(client, settings);
                        return (Client: client, Data: (JsonObject?)await session.CallAtAsync(Levels.System, Cmd.WindowsUpdateStatus), Error: "");
                    }
                    catch (Exception ex) { return (Client: client, Data: (JsonObject?)null, Error: ex.Message); }
                }));
                var lines = answers.Select(answer => answer.Data is null
                    ? $"• {answer.Client.Name}: not read - {answer.Error}"
                    : $"• {answer.Client.Name}: {Line(answer.Data)}").ToList();
                form.Note(string.Join("\n", lines.Take(40)) + (lines.Count > 40 ? $"\n... and {lines.Count - 40} more" : ""));
                if (unsupported.Count > 0)
                    form.Note($"Left out, the Client needs updating first: {string.Join(", ", unsupported.Take(10).Select(client => client.Name))}{(unsupported.Count > 10 ? ", ..." : "")}");
                foreach (var answer in answers.Where(answer => answer.Data is not null))
                {
                    var version = answer.Data![WindowsRelease.Version]?.GetValue<string>() ?? "";
                    if (WindowsRelease.Compare(newest, version) < 0 || newest.Length == 0) newest = WindowsRelease.Normalize(version);
                }
                form.Combo("action", "_Action", [Move, Remove])
                    .Text("version", "Windows _version to move to (for example 25H2)", newest)
                    .Note("A new Windows version waits in Settings behind \"Download and install\" and an update check does not list it. " +
                          "With a target set, Windows offers that version as an ordinary update: check for updates again after a few minutes and install it with the others. " +
                          "It is a large download and needs a restart. The computer then stays on that version until you change or remove the target here. " +
                          "Home editions ignore a target, and a computer whose hardware Windows does not accept for that version is not moved.");
            });
        if (!dlg.Ask("Apply")) return false;

        bool remove = dlg.S("action") == Remove;
        var target = remove ? "" : WindowsRelease.Normalize(dlg.S("version"));
        if (!remove && target.Length == 0)
        {
            MessageBox.Show(owner, "Type a Windows version name such as 25H2.", Caption, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (MessageBox.Show(owner,
                remove ? $"Remove the Windows target version on {targets.Count} computer(s)?"
                       : $"Make {targets.Count} computer(s) move to Windows version {target} and stay on it?\n\nWindows downloads it on its own schedule or when you install updates; nothing restarts now.",
                Caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return false;

        var results = await Task.WhenAll(targets.Select(async client =>
        {
            try
            {
                using var session = await ClientSession.OpenAsync(client, settings);
                await session.CallAtAsync(Levels.System, Cmd.WindowsUpdateTargetSet, new JsonObject { ["version"] = target });
                return (Client: client, Error: "");
            }
            catch (Exception ex) { return (Client: client, Error: ex.Message); }
        }));
        var failed = results.Where(result => result.Error.Length > 0).ToList();
        var done = results.Length - failed.Count;
        MessageBox.Show(owner,
            (remove ? $"Target removed on {done} of {results.Length} computer(s)." : $"Target {target} set on {done} of {results.Length} computer(s).") +
            (failed.Count == 0 ? "" : "\n\nNot changed:\n" + string.Join("\n", failed.Take(15).Select(result => $"• {result.Client.Name}: {result.Error}"))) +
            (remove || done == 0 ? "" : "\n\nCheck for updates again in a few minutes; the new Windows version should then be in the list."),
            Caption, MessageBoxButton.OK, failed.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        return done > 0;
    }

    /// <summary>One computer's version and target as a single line.</summary>
    public static string Line(JsonObject status)
    {
        var version = WindowsRelease.Describe(status);
        if (version.Length == 0) return "Windows version not reported (update the Client)";
        var target = WindowsRelease.DescribeTarget(status);
        return target.Length == 0 ? version : $"{version} · {target}";
    }
}
