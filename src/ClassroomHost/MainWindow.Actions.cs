// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using ClassroomManager.Shared;
#if BROWSER_INVENTORY_MODULE
using ClassroomManager.BrowserInventory;
#endif
#if REPORTING_MODULE
using ClassroomManager.Reporting;
#endif

namespace ClassroomManager.Host;

public partial class MainWindow
{
    void Command_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        new CommandWindow(targets, _settings, this).Show();
    }

    async void OpenWebsite_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0 || !EnsureFeatureSupported(targets, Cmd.WebsiteOpen, "Open website")) return;
        var history = _settings.SavedWebsites.Select(value => new LaunchHistoryChoice(value, "", true))
            .Concat(_settings.RecentWebsites.Select(value => new LaunchHistoryChoice(value, "", false))).ToList();
        var dlg = new LaunchTargetDialog("Open website", "_Website address", "https://", history, false,
            "Choose a saved or recent address, or type a new one. It opens in the browser you choose, or in each signed-in user's default browser; a browser that is not installed on a computer is replaced there by its default one.", this,
            new WebsiteChoices(_settings.OpenWebsiteMaximized, _settings.OpenWebsiteBrowser));
        if (dlg.ShowDialog() != true) return;
        if (dlg.Choices is { } choices) (_settings.OpenWebsiteMaximized, _settings.OpenWebsiteBrowser) = (choices.Maximized, choices.BrowserId);
        var url = dlg.Target;
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(this, "Enter a valid HTTP or HTTPS website address.");
            return;
        }
        _settings.RememberWebsite(uri.AbsoluteUri, dlg.SaveForLater);
        _settings.Save();
        await OpenWebsiteOnAsync(targets, uri.AbsoluteUri, "Open website");
    }

    /// <summary>
    /// Opens an address on the computers in the browser and window state the teacher last chose. A Client older
    /// than protocol 1.15 cannot honour the choice and opens it in the default browser as before; the log says which.
    /// </summary>
    Task OpenWebsiteOnAsync(List<ClientInfo> targets, string absoluteUri, string title)
    {
        var browser = _settings.OpenWebsiteBrowser;
        var maximized = _settings.OpenWebsiteMaximized;
        var older = targets.Where(client => !client.SupportsWebsiteOptions).ToList();
        if (older.Count > 0 && (browser.Length > 0 || maximized))
            Log($"{older.Count} computer(s) have an older Client ({string.Join(", ", older.Take(5).Select(client => client.Name))}{(older.Count > 5 ? ", ..." : "")}): the address opens there in the default browser, as before. Update the Client to use the browser and window choice.");
        return RunOnAsync(targets, title, async (client, session) =>
        {
            var answer = await session.CallAtAsync(Levels.User, Cmd.WebsiteOpen, WebsiteLaunch.Args(absoluteUri, browser, maximized, client.SupportsWebsiteOptions));
            if (answer[WebsiteLaunch.FellBack]?.GetValue<bool>() == true)
                Log($"  {client.Name}: {BrowserCatalog.Find(browser)?.Name ?? browser} is not installed there; opened in the default browser.");
            else if (maximized && browser.Length > 0 && answer[WebsiteLaunch.WasMaximized]?.GetValue<bool>() == false)
                Log($"  {client.Name}: opened, but the window could not be confirmed as maximized.");
        });
    }

    async void OpenProgram_Click(object sender, RoutedEventArgs e) => await OpenProgramOnAsync(Selected(), "notepad.exe");

    /// <summary>Open program on given computers, always as the signed-in user; elevation stays a manual step on the computer.</summary>
    async Task OpenProgramOnAsync(IReadOnlyList<ClientInfo> computers, string suggested)
    {
        var targets = computers.ToList();
        if (targets.Count == 0 || !EnsureFeatureSupported(targets, Cmd.ProgramOpen, "Open program")) return;
        var history = _settings.SavedPrograms.Select(value => new LaunchHistoryChoice(value.Program, value.Arguments, true))
            .Concat(_settings.RecentPrograms.Select(value => new LaunchHistoryChoice(value.Program, value.Arguments, false))).ToList();
        var title = targets.Count == 1 ? $"Open program - {targets[0].Name}" : $"Open program - {targets.Count} computers";
        var dlg = new LaunchTargetDialog(title, "_Program path or executable name", suggested.Length > 0 ? suggested : "notepad.exe", history, true,
            "Choose a saved or recent program, or type a new one. It starts as the signed-in user; environment variables such as %WINDIR% are supported.", this);
        if (dlg.ShowDialog() != true || dlg.Target.Length == 0) return;
        _settings.RememberProgram(dlg.Target, dlg.Arguments, dlg.SaveForLater);
        _settings.Save();
        await RunOnAsync(targets, "Open program", (client, session) =>
            session.CallAtAsync(Levels.User, Cmd.ProgramOpen, new JsonObject
            {
                ["program"] = dlg.Target,
                ["arguments"] = dlg.Arguments,
            }));
    }

    async void CancelPower_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count > 0) await DoCancelPowerAsync(targets);
    }

    async void Power_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;

        var targetLabel = targets.Count == 1 ? targets[0].Name : $"{targets.Count} computers";
        var dlg = new FormDialog($"Power - {targetLabel}", this)
            .Target(targets)
            .Combo("action", "Action", new[] { "Shut down", "Restart", "Sleep", "Log off user", "Cancel pending shutdown/restart" })
            .Text("delay", "Delay in seconds (shut down / restart)", PowerDelays.Default("Shut down"))
            .Text("msg", "Message shown to students", "This computer will shut down soon. Please save your work.")
            .Suggest("action", "delay", PowerDelays.Default);
        if (!dlg.Ask("Send")) return;

        var action = dlg.S("action") switch
        {
            "Shut down" => "shutdown",
            "Restart" => "restart",
            "Sleep" => "sleep",
            "Log off user" => "logoff",
            _ => "cancel",
        };
        int.TryParse(dlg.S("delay"), out var delay);

        if (action is "shutdown" or "restart" or "sleep" or "logoff" &&
            MessageBox.Show(this, $"{dlg.S("action")} on {targets.Count} computer(s)? Unsaved work may be lost.",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        await DoPowerAsync(targets, action, delay, dlg.S("msg"));
    }

    // ---- files ----------------------------------------------------------------------------

    async void Message_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;

        var dlg = new FormDialog("Send message", this)
            .Target(targets)
            .Text("title", "Title", "Message from your teacher")
            .Text("text", "Message")
            .Text("secs", "Close automatically after (seconds, 0 = stay until OK)", "0")
            .Require("text", "Enter the message to send.");
        if (!dlg.Ask("Send")) return;
        int.TryParse(dlg.S("secs"), out var secs);

        await DoMessageAsync(targets, dlg.S("title"), dlg.S("text"), secs);
    }

    async void HideMessages_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0 || !EnsureFeatureSupported(targets, Cmd.MessageClear, "Hide Host messages")) return;
        await RunOnAsync(targets, "Hide messages", (client, session) =>
            session.CallAtAsync(Levels.User, Cmd.MessageClear), allowRetry: true);
    }

    async void Lock_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;

        var dlg = new FormDialog("Lock screens", this)
            .Target(targets)
            .Text("title", "Title", "Screen locked")
            .Text("msg", "Message shown to students", "Please look at the teacher.")
            .Note("Covers every monitor and blocks Win / Alt+Tab / Alt+F4. It stays on until you press Unlock " +
                  "and is re-applied if the student signs in again.");
        if (!dlg.Ask("Lock")) return;

        await DoLockAsync(targets, dlg.S("title"), dlg.S("msg"));
    }

    async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        await DoUnlockAsync(targets);
    }

    // ---- desktop ---------------------------------------------------------------------------------

    Task DoLockAsync(List<ClientInfo> t, string title, string msg) =>
        RunOnAsync(t, "Lock screen", (c, s) => s.CallAtAsync(Levels.User, Cmd.LockSet,
            new JsonObject { ["title"] = title, ["message"] = msg }), allowRetry: true,
            undo: ("Unlock screen", (c, s) => s.CallAtAsync(Levels.User, Cmd.LockClear)));

    Task DoUnlockAsync(List<ClientInfo> t) =>
        RunOnAsync(t, "Unlock screen", (c, s) => s.CallAtAsync(Levels.User, Cmd.LockClear), allowRetry: true);

    Task DoMessageAsync(List<ClientInfo> t, string title, string text, int seconds)
    {
        // Every way of sending a message ends here, a quick action with no text among them.
        if (string.IsNullOrWhiteSpace(text))
        {
            Log("Message not sent: it has no text.");
            MessageBox.Show(this, "The message has no text, so nothing was sent.", "Send message", MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.CompletedTask;
        }
        return SendMessageAsync(t, title, text, seconds);
    }

    Task SendMessageAsync(List<ClientInfo> t, string title, string text, int seconds) =>
        RunOnAsync(t, "Message", (c, s) => s.CallAtAsync(Levels.User, Cmd.MessageShow,
            new JsonObject { ["title"] = title, ["text"] = text, ["seconds"] = seconds }),
            undo: ("Hide messages", (c, s) => s.CallAtAsync(Levels.User, Cmd.MessageClear)));

    Task DoPowerAsync(List<ClientInfo> t, string action, int delay, string msg)
    {
        AutorunSnapshotCache.Invalidate(t);
        TaskSnapshotCache.Invalidate(t);
        // A shutdown or restart with a delay can still be called off; anything else has happened by the time it is seen.
        (string, Func<ClientInfo, ClientSession, Task>)? callOff = action is "shutdown" or "restart" && delay >= PowerDelays.UndoMinimum
            ? ("Cancel pending shutdown/restart", (c, s) => s.CallAsync(Cmd.PowerAction, new JsonObject { ["action"] = "cancel" }))
            : null;
        return RunOnAsync(t, "Power: " + action, (c, s) => s.CallAsync(Cmd.PowerAction,
            new JsonObject { ["action"] = action, ["delaySec"] = delay, ["message"] = msg }), undo: callOff);
    }

    Task DoCancelPowerAsync(List<ClientInfo> t) =>
        RunOnAsync(t, "Cancel pending shutdown/restart", (c, s) => s.CallAsync(Cmd.PowerAction, new JsonObject { ["action"] = "cancel" }),
            allowRetry: true);

    Task DoCommandAsync(List<ClientInfo> t, string command) =>
        RunOnAsync(t, "Command", async (c, s) =>
        {
            var data = await s.CallAtAsync(Levels.System, Cmd.CmdRun,
                new JsonObject { ["shell"] = "cmd", ["command"] = command, ["timeoutSec"] = 300 });
            Log($"  {c.Name}: exit code {data["exitCode"]?.GetValue<int>()}");
        });

    async Task DoWakeAsync(List<ClientInfo> targets)
    {
        Log($"Wake up: {targets.Count} computer(s)...");
        var sent = 0;
        foreach (var c in targets)
        {
            try
            {
                await WakeOnLan.SendAsync(c.Mac, c.Address);
                sent++;
                Log($"  sent   {c.Name}");
            }
            catch (Exception ex) { Log($"  FAIL   {c.Name}: {ex.Message}"); }
        }
        Log($"Wake-up packets sent for {sent} of {targets.Count} computer(s). Computers need Wake-on-LAN enabled in BIOS and network adapter settings.");

        // A computer that is already on has nothing to wake but its screen: stop the screen saver and light the display.
        var awake = targets.Where(c => c.Status.StartsWith("Online") && c.User.Length > 0 && c.SupportsCommand(Cmd.DisplayWake)).ToList();
        if (awake.Count == 0) return;
        await RunOnAsync(awake, "Wake screen", async (c, s) =>
            Log($"  {c.Name}: {DisplayWakeRules.Describe(await s.CallAtAsync(Levels.User, Cmd.DisplayWake))}"));
    }

    // ---- schedules ----------------------------------------------------------------------------------

    async void SyncTime_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;

        var dlg = new FormDialog("Sync time", this)
            .Target(targets)
            .Combo("mode", "Set the clock from", new[]
            {
                "This computer's clock (works without internet)",
                "The Windows time service (w32tm /resync, needs a reachable time server)",
            })
            .Check("tz", $"Also set the time zone to match this computer ({TimeZoneInfo.Local.DisplayName})")
            .Note("The clock is set as the service on each student PC (SYSTEM). Accuracy is typically well under a second.");
        if (!dlg.Ask("Sync")) return;

        await DoTimeSyncAsync(targets, dlg.S("mode").StartsWith("This"), dlg.B("tz"));
    }

    async Task DoTimeSyncAsync(List<ClientInfo> targets, bool fromHost, bool timezone)
    {
        await RunOnAsync(targets, "Sync time", async (c, s) =>
        {
            var args = new JsonObject { ["mode"] = fromHost ? "host" : "ntp" };
            if (fromHost) args["utcTicks"] = DateTime.UtcNow.Ticks;   // stamped right before sending
            if (timezone) args["timezone"] = TimeZoneInfo.Local.Id;
            var data = await s.CallAsync(Cmd.TimeSync, args);
            Log($"  {c.Name}: {data["note"]?.GetValue<string>()}");
        }, allowRetry: true);
        await CheckAllAsync();   // show the new clock difference
    }
}
