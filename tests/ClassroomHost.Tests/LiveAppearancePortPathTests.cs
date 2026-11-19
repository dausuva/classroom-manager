// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Appearance;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;
using Xunit.Abstractions;

namespace ClassroomHost.Tests;

public sealed class LiveAppearancePortPathFactAttribute : FactAttribute
{
    public LiveAppearancePortPathFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLASSROOM_APPEARANCE_PORT_TEST_ADDRESS")))
            Skip = "Opt-in: changes and resets the appearance settings of one installed test Client through the module ports. Use a test computer with a signed-in user.";
    }
}

/// <summary>
/// The Appearance catalogue on a real Client, through <see cref="ModuleRuns"/> and the broker: every setting is set to every value it offers,
/// read back, and reset; and the effects that only the user's desktop can show (the icons, the taskbar colour) are measured in the user's
/// own session. Everything is reset at the end, whatever happens.
/// </summary>
[Collection(nameof(HostBackupTests))]
public sealed class LiveAppearancePortPathTests(ITestOutputHelper output)
{
    // Runs in the signed-in user's session: how many icons the desktop list shows and whether the list is visible at all.
    const string DesktopProbe = """
        Add-Type @"
        using System; using System.Runtime.InteropServices;
        public class DeskProbe {
          public delegate bool EnumProc(IntPtr h, IntPtr p);
          [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
          [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
          [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
          [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
          [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
          public static IntPtr View() {
            var pm = FindWindow("Progman", null);
            var v = pm == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(pm, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (v != IntPtr.Zero) return v;
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, p) => { var c = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null); if (c == IntPtr.Zero) return true; found = c; return false; }, IntPtr.Zero);
            return found;
          }
          public static string Probe() {
            var v = View(); if (v == IntPtr.Zero) return "noview 0";
            var l = FindWindowEx(v, IntPtr.Zero, "SysListView32", null); if (l == IntPtr.Zero) return "nolist 0";
            var n = SendMessage(l, 0x1004, IntPtr.Zero, IntPtr.Zero).ToInt64();
            return (IsWindowVisible(l) ? "visible " : "hidden ") + n;
          }
        }
        "@
        [DeskProbe]::Probe()
        """;

    // Runs in the signed-in user's session: what Windows itself reports as the wallpaper position (its own COM interface, so this is the
    // shell's live state, not a registry value) and the background colour a new app is given (black in dark mode, white in light mode).
    const string ShellProbe = """
        Add-Type @"
        using System; using System.Runtime.InteropServices;
        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDesktopWallpaper {
          void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
          [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
          [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
          uint GetMonitorDevicePathCount();
          void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out int l, out int t, out int r, out int b);
          void SetBackgroundColor(uint color);
          uint GetBackgroundColor();
          void SetPosition(int position);
          int GetPosition();
        }
        [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")] public class DesktopWallpaperClass { }
        public static class ShellProbe { public static int Position() { return ((IDesktopWallpaper)new DesktopWallpaperClass()).GetPosition(); } }
        "@
        [void][Windows.UI.ViewManagement.UISettings, Windows.UI.ViewManagement, ContentType = WindowsRuntime]
        $c = (New-Object Windows.UI.ViewManagement.UISettings).GetColorValue([Windows.UI.ViewManagement.UIColorType]::Background)
        "shell=position:$([ShellProbe]::Position()) background:$($c.R)"
        """;

    // Runs in the signed-in user's session: what the taskbar shows (found through UI Automation, so it is the live shell, not a registry value)
    // and the system's own animation switch.
    const string TaskbarProbe = """
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint a, uint p, ref int v, uint f);' -Name S -Namespace N
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $tray = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, "Shell_TrayWnd")))
        $all = $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $byId = @{}
        foreach ($e in $all) { $id = $e.Current.AutomationId; if ($id -and -not $byId.ContainsKey($id)) { $byId[$id] = $e } }
        $clock = ($all | Where-Object { $_.Current.Name -match '\d\d:\d\d' } | Select-Object -First 1).Current.Name
        $startElement = if ($byId.ContainsKey('StartButton')) { $byId['StartButton'] } else { $all | Where-Object { $_.Current.BoundingRectangle.X -eq 0 -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Pane' -and $_.Current.BoundingRectangle.Width -lt 80 } | Select-Object -First 1 }
        $searchWidth = if ($byId.ContainsKey('SearchButton')) { [int]$byId['SearchButton'].Current.BoundingRectangle.Width } else { -1 }
        $animation = 0; [void][N.S]::SystemParametersInfo(0x1042, 0, [ref]$animation, 0)
        "taskbar=start.x:$([int]$startElement.Current.BoundingRectangle.X) search.w:$searchWidth taskview:$($byId.ContainsKey('TaskViewButton') -or $byId.ContainsKey('4113')) news:$($byId.ContainsKey('4105')) seconds:$($clock -match '\d\d:\d\d:\d\d') animation:$animation"
        """;

    // The size of the main display as Windows' video controller reports it now.
    const string DisplayProbe = """
        $v = Get-CimInstance Win32_VideoController | Where-Object { $_.CurrentHorizontalResolution } | Select-Object -First 1
        "display=$($v.CurrentHorizontalResolution)x$($v.CurrentVerticalResolution)"
        """;

    // Presses the "Keep changes" button of the question the computer asks the person (found through UI Automation).
    const string KeepProbe = """
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $button = $null
        for ($i = 0; $i -lt 20 -and $null -eq $button; $i++) {
          $button = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "KeepButton")))
          if ($null -eq $button) { Start-Sleep -Milliseconds 250 }
        }
        if ($null -eq $button) { "keep=missing" } else { $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); "keep=clicked" }
        """;

    [LiveAppearancePortPathFact]
    public async Task Every_setting_can_be_set_read_back_and_reset_and_the_desktop_shows_the_icons_and_the_taskbar_colour_it_was_told_to()
    {
        var address = Environment.GetEnvironmentVariable("CLASSROOM_APPEARANCE_PORT_TEST_ADDRESS")!;
        var settingsPath = Environment.GetEnvironmentVariable("CLASSROOM_BACKUP_TEST_SETTINGS");
        Assert.False(string.IsNullOrWhiteSpace(settingsPath));
        var before = File.ReadAllBytes(settingsPath!);
        var keysPath = Path.ChangeExtension(settingsPath!, ".keys");
        var keysBefore = File.ReadAllBytes(keysPath);
        var suspended = HostSettings.SavesSuspended;
        HostSettings.SavesSuspended = true;
        try
        {
            var settings = HostSettings.Load(settingsPath);
            var saved = Assert.Single(settings.Computers, c => c.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            var client = new ClientInfo { Address = saved.Address, Port = saved.Port, ClientId = saved.ClientId,
                Mac = saved.Mac, KeyId = saved.KeyId, ComputerName = saved.Name };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var session = await ClientSession.OpenAsync(client, settings, deadline.Token);
            var ping = await session.CallAsync(Cmd.Ping, ct: deadline.Token);
            client.Version = ping["version"]?.GetValue<string>() ?? "";
            client.User = ping["userName"]?.GetValue<string>() ?? "";
            Assert.NotEmpty(client.User);
            Assert.True(client.SupportsCommand(Cmd.AppearanceCatalog), $"client {client.Version} is too old");
            var (appearance, machine) = ModuleRuns.OpenAppearance(session, client);

            var catalogue = await appearance.CatalogAsync(machine, deadline.Token);
            output.WriteLine($"{client.Name}: client {client.Version}, Windows {catalogue.Windows} (build {catalogue.Build}), {catalogue.Settings.Count} setting(s)");
            Assert.NotEmpty(catalogue.Settings);
            Assert.DoesNotContain(catalogue.Settings, setting => setting.Changed);
            var ids = catalogue.Settings.Select(setting => setting.Id).ToList();
            var original = catalogue.Settings.ToDictionary(setting => setting.Id, setting => setting.Value);
            try
            {
                foreach (var setting in catalogue.Settings.Where(setting => setting.Risk != "confirm"))
                {
                    var values = setting.Kind == AppearanceWireReader.KindToggle ? ["true", "false"] : setting.Choices.Select(choice => choice.Value).ToArray();
                    foreach (var value in values)
                    {
                        var result = Assert.Single(await appearance.SetAsync(machine, new Dictionary<string, string> { [setting.Id] = value }, deadline.Token));
                        Assert.True(result.Status is "applied" or "unchanged", $"{setting.Id} = {value}: {result.Status} {result.Reason}");
                        var shown = (await appearance.GetAsync(machine, [setting.Id], deadline.Token))[setting.Id];
                        Assert.Equal(value, shown);
                    }
                    output.WriteLine($"  {setting.Id}: every value set and read back ({string.Join(", ", values)}); was {original[setting.Id]}");
                }

                // The desktop: icons on, off, and one system icon at a time. Measured in the user's own session.
                await Set(appearance, machine, deadline.Token, ("desktop.show-icons", "true"), ("desktop.icon.recycle-bin", "true"));
                var shownIcons = await Probe(session, DesktopProbe, deadline.Token);
                await Set(appearance, machine, deadline.Token, ("desktop.icon.recycle-bin", "false"));
                var withoutBin = await Probe(session, DesktopProbe, deadline.Token);
                await Set(appearance, machine, deadline.Token, ("desktop.icon.recycle-bin", "true"), ("desktop.icon.this-pc", "true"));
                var withThisPc = await Probe(session, DesktopProbe, deadline.Token);
                await Set(appearance, machine, deadline.Token, ("desktop.show-icons", "false"));
                var hiddenAll = await Probe(session, DesktopProbe, deadline.Token);
                output.WriteLine($"Desktop: icons on = [{shownIcons}]; Recycle Bin off = [{withoutBin}]; This PC on = [{withThisPc}]; icons off = [{hiddenAll}]");

                // What Windows itself reports: the app background in light and dark mode, and the wallpaper position for each fit.
                await Set(appearance, machine, deadline.Token, ("desktop.show-icons", "true"), ("colors.app-mode", "light"));
                var light = await Probe(session, ShellProbe, deadline.Token);
                await Set(appearance, machine, deadline.Token, ("colors.app-mode", "dark"));
                var dark = await Probe(session, ShellProbe, deadline.Token);
                output.WriteLine($"App mode light: {light}; dark: {dark}");
                var positions = new Dictionary<string, string> { ["fill"] = "4", ["fit"] = "3", ["stretch"] = "2", ["tile"] = "1", ["center"] = "0", ["span"] = "5" };
                var seen = new Dictionary<string, string>();
                foreach (var fit in positions.Keys)
                {
                    await Set(appearance, machine, deadline.Token, ("desktop.wallpaper-fit", fit));
                    seen[fit] = await Probe(session, ShellProbe, deadline.Token);
                }
                foreach (var (fit, probe) in seen) output.WriteLine($"Wallpaper {fit,-8}: {probe}");

                // The taskbar and the animation switch, as Windows reports them. Only what this Windows version has is changed.
                var settingIds = catalogue.Settings.Select(setting => setting.Id).ToHashSet();
                var windows11 = catalogue.Windows == "11";
                if (settingIds.Contains("taskbar.task-view"))
                {
                    var hasSeconds = settingIds.Contains("taskbar.clock-seconds");
                    await Set(appearance, machine, deadline.Token, [("taskbar.task-view", "true"), ("effects.animations", "true"), .. hasSeconds ? new[] { ("taskbar.clock-seconds", "false") } : []]);
                    var taskbarOn = await Probe(session, TaskbarProbe, deadline.Token);
                    await Set(appearance, machine, deadline.Token, [("taskbar.task-view", "false"), ("effects.animations", "false"), .. hasSeconds ? new[] { ("taskbar.clock-seconds", "true") } : []]);
                    var taskbarOff = await Probe(session, TaskbarProbe, deadline.Token);
                    output.WriteLine($"Taskbar, task view on / seconds off / animation on: {taskbarOn}");
                    output.WriteLine($"Taskbar, task view off / seconds on / animation off: {taskbarOff}");
                    Assert.Contains("taskview:True", taskbarOn);
                    Assert.Contains("taskview:False", taskbarOff);
                    if (hasSeconds)
                    {
                        Assert.Contains("seconds:False", taskbarOn);
                        Assert.Contains("seconds:True", taskbarOff);
                    }
                    Assert.Contains("animation:1", taskbarOn);
                    Assert.Contains("animation:0", taskbarOff);
                    if (windows11)
                    {
                        await Set(appearance, machine, deadline.Token, ("taskbar.alignment", "left"), ("taskbar.search", "hidden"));
                        var hidden = await Probe(session, TaskbarProbe, deadline.Token);
                        await Set(appearance, machine, deadline.Token, ("taskbar.alignment", "center"), ("taskbar.search", "icon"));
                        var icon = await Probe(session, TaskbarProbe, deadline.Token);
                        await Set(appearance, machine, deadline.Token, ("taskbar.search", "box"));
                        var box = await Probe(session, TaskbarProbe, deadline.Token);
                        output.WriteLine($"Taskbar, left / search hidden: {hidden}");
                        output.WriteLine($"Taskbar, centre / search icon: {icon}");
                        output.WriteLine($"Taskbar, centre / search box:  {box}");
                        Assert.Contains("search.w:-1", hidden);
                        Assert.InRange(StartX(hidden), 0, 150);
                        Assert.InRange(StartX(icon), 300, 3000);
                        Assert.InRange(SearchWidth(icon), 20, 120);
                        Assert.InRange(SearchWidth(box), 150, 600);
                    }
                }

                // The display: a change that nobody confirms goes back by itself; one the person keeps stays until it is reset.
                if (catalogue.Settings.FirstOrDefault(setting => setting.Id == "display.size") is { } size && size.Choices.Count > 1)
                {
                    var startSize = await Probe(session, DisplayProbe, deadline.Token);
                    var other = size.Choices.Select(choice => choice.Value).Where(value => value != size.Value).Where(value => !value.StartsWith("800x") && !value.StartsWith("640x"))
                        .OrderBy(value => int.Parse(value.Split('x')[0])).Skip(1).First();
                    var asked = Assert.Single(await appearance.SetAsync(machine, new Dictionary<string, string> { ["display.size"] = other }, deadline.Token));
                    Assert.Equal("applied", asked.Status);
                    Assert.True(asked.ConfirmSeconds > 0);
                    await Task.Delay(2500, deadline.Token);
                    var changed = await Probe(session, DisplayProbe, deadline.Token);
                    await Task.Delay(TimeSpan.FromSeconds(asked.ConfirmSeconds + 4), deadline.Token);
                    var reverted = await Probe(session, DisplayProbe, deadline.Token);
                    output.WriteLine($"Display: {startSize}; set {other}: {changed}; after {asked.ConfirmSeconds} s with no answer: {reverted}");
                    Assert.NotEqual(startSize, changed);
                    Assert.Equal(startSize, reverted);
                    Assert.Equal(size.Value, (await appearance.GetAsync(machine, ["display.size"], deadline.Token))["display.size"]);

                    var kept = Assert.Single(await appearance.SetAsync(machine, new Dictionary<string, string> { ["display.size"] = other }, deadline.Token));
                    Assert.Equal("applied", kept.Status);
                    await Task.Delay(2500, deadline.Token);
                    var pressed = await Probe(session, KeepProbe, deadline.Token);
                    await Task.Delay(TimeSpan.FromSeconds(kept.ConfirmSeconds + 4), deadline.Token);
                    var stayed = await Probe(session, DisplayProbe, deadline.Token);
                    output.WriteLine($"Display, kept: {pressed}; after the time: {stayed}");
                    Assert.Equal("keep=clicked", pressed);
                    Assert.NotEqual(startSize, stayed);
                    Assert.Equal(other, (await appearance.GetAsync(machine, ["display.size"], deadline.Token))["display.size"]);
                    var back = Assert.Single(await appearance.ResetAsync(machine, ["display.size"], deadline.Token));
                    Assert.Equal("restored", back.Status);
                    await Task.Delay(2500, deadline.Token);
                    var restored = await Probe(session, DisplayProbe, deadline.Token);
                    output.WriteLine($"Display after reset: {restored}");
                    Assert.Equal(startSize, restored);
                }

                Assert.StartsWith("visible", shownIcons);
                Assert.StartsWith("hidden", hiddenAll);
                Assert.Equal(Count(shownIcons) - 1, Count(withoutBin));
                Assert.Equal(Count(shownIcons) + 1, Count(withThisPc));
                Assert.Contains("background:255", light);
                Assert.Contains("background:0", dark);
                foreach (var (fit, position) in positions) Assert.Contains("position:" + position + " ", seen[fit] + " ");
            }
            finally
            {
                var reset = await appearance.ResetAsync(machine, ids, CancellationToken.None);
                output.WriteLine("Reset: " + string.Join(", ", reset.GroupBy(result => result.Status).Select(group => $"{group.Count()} {group.Key}")));
            }

            var after = await appearance.CatalogAsync(machine, CancellationToken.None);
            Assert.DoesNotContain(after.Settings, setting => setting.Changed);
            Assert.Equal(original, after.Settings.ToDictionary(setting => setting.Id, setting => setting.Value));
            var icons = await Probe(session, DesktopProbe, CancellationToken.None);
            output.WriteLine($"Desktop after reset: [{icons}]");
        }
        finally
        {
            HostSettings.SavesSuspended = suspended;
            Assert.Equal(before, File.ReadAllBytes(settingsPath!));
            Assert.Equal(keysBefore, File.ReadAllBytes(keysPath));
        }
    }

    static async Task Set(AppearanceOperations appearance, ClassroomManager.Modules.ModuleComputer machine, CancellationToken cancellation, params (string Id, string Value)[] values)
    {
        var results = await appearance.SetAsync(machine, values.ToDictionary(item => item.Id, item => item.Value), cancellation);
        Assert.All(results, result => Assert.True(result.Status is "applied" or "unchanged", $"{result.Id}: {result.Status} {result.Reason}"));
        await Task.Delay(1200, cancellation);
    }

    // Runs a PowerShell script as the signed-in user and returns the one line it printed (PowerShell adds progress XML around it).
    // The screen saver is stopped first: a screenshot of a screen saver says nothing about the desktop.
    static async Task<string> Probe(ClientSession session, string script, CancellationToken cancellation)
    {
        await session.CallAtAsync(Levels.User, Cmd.DisplayWake, ct: cancellation);
        await Task.Delay(800, cancellation);
        var data = await session.CallAtAsync(Levels.User, Cmd.CmdRun, new JsonObject { ["shell"] = "powershell", ["command"] = script, ["timeoutSec"] = 60 }, cancellation);
        Assert.False(data["timedOut"]?.GetValue<bool>() == true, "the probe timed out");
        var lines = (data["output"]?.GetValue<string>() ?? "").Split((char)10).Select(line => line.Trim());
        return lines.FirstOrDefault(line => line.StartsWith("visible ") || line.StartsWith("hidden ") || line.StartsWith("noview") || line.StartsWith("nolist") || line.StartsWith("shell=") || line.StartsWith("taskbar=") || line.StartsWith("display=") || line.StartsWith("keep=")) ?? "";
    }

    static int StartX(string probe) => int.Parse(System.Text.RegularExpressions.Regex.Match(probe, @"start\.x:(-?\d+)").Groups[1].Value);

    static int SearchWidth(string probe) => int.Parse(System.Text.RegularExpressions.Regex.Match(probe, @"search\.w:(-?\d+)").Groups[1].Value);

    static int Count(string probe) => int.Parse(probe.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1]);

}
