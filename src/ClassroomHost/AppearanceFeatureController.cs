// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public delegate Task AppearanceOperationRunner(List<ClientInfo> targets, string title, Func<ClientInfo, ClientSession, Task> action,
    bool allowRetry = false, string warnings = "", (string Label, Func<ClientInfo, ClientSession, Task> Action)? undo = null);

/// <summary>Feature-scoped UI composition; typed sequencing lives in the Appearance assembly and privileged adapters stay in Host.</summary>
public sealed class AppearanceFeatureController(Window owner, HostSettings settings, Func<List<ClientInfo>> selected,
    Func<List<ClientInfo>, string, string, bool> supports, AppearanceOperationRunner run)
{
    readonly Window _owner = owner;
    readonly HostSettings _settings = settings;
    readonly Func<List<ClientInfo>> _selected = selected;
    readonly Func<List<ClientInfo>, string, string, bool> _supports = supports;
    readonly AppearanceOperationRunner _run = run;
    string MyStaging => StagingNamespace.Root(_settings);

    public async Task WallpaperAsync()
    {
        var targets = _selected();
        if (targets.Count == 0) return;

        var picker = new WallpaperLibraryWindow(_owner);
        if (picker.ShowDialog() != true) return;

        var place = picker.SelectedTarget;
        var userOnly = picker.SignInForSignedInUserOnly;
        if (userOnly)
        {
            if (!_supports(targets, Cmd.LockScreenSet, "Lock screen picture of the signed-in user")) return;
            if (MessageBox.Show(_owner,
                    "The picture is set for the user signed in on each selected computer, also on Windows Home, and appears " +
                    "the next time the computer is locked. Other users of the computer keep theirs.\n\nContinue?",
                    "Lock screen picture", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        }
        else if (place.IncludesSignIn())
        {
            if (!_supports(targets, Cmd.SignInImageSet, "Lock and sign-in screen image")) return;
            if (MessageBox.Show(_owner,
                    "The lock and sign-in screen picture is one setting for every user of each selected computer, " +
                    "and it appears the next time the computer is locked or shows the sign-in screen.\n\n" +
                    "Computers where Group Policy sets this picture refuse the change. Windows Home refuses a picture; there, " +
                    "Windows default puts the signed-in user's own lock screen back to Windows spotlight.\n\nContinue?",
                    "Lock and sign-in screen image", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        }

        if (picker.SelectedKind == WallpaperChoiceKind.WindowsDefault)
        {
            if (place.IncludesDesktop() && !_supports(targets, Cmd.WallpaperDefault, "Windows default wallpaper")) return;
            await _run(targets, WallpaperTargets.OperationTitle(place, windowsDefault: true),
                (client, session) => new ClientAppearanceActions(client, session, MyStaging)
                    .ChangeWallpaperAsync(new(place, true, userOnly)), allowRetry: true);
            return;
        }
        if (picker.SelectedKind == WallpaperChoiceKind.SolidColor)
        {
            if (!_supports(targets, Cmd.WallpaperColor, "Solid-color wallpaper")) return;
            await _run(targets, $"Set solid background #{picker.SelectedColor}",
                (_, session) => session.CallAtAsync(Levels.User, Cmd.WallpaperColor,
                    new JsonObject { ["color"] = picker.SelectedColor }), allowRetry: true);
            return;
        }

        var file = picker.SelectedFile;
        var style = picker.SelectedStyle.ToLowerInvariant();

        var sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))).ToLowerInvariant();
        await _run(targets, WallpaperTargets.OperationTitle(place, windowsDefault: false),
            (client, session) => new ClientAppearanceActions(client, session, MyStaging)
                .ChangeWallpaperAsync(new(place, false, userOnly, file, style, sha256)),
            undo: place == WallpaperTarget.Desktop
                ? ("Restore previous wallpaper", (client, session) => session.CallAtAsync(Levels.User, Cmd.WallpaperReset))
                : null);
    }

    public async Task WallpaperResetAsync()
    {
        var targets = _selected();
        if (targets.Count == 0) return;
        if (MessageBox.Show(_owner,
                "Restore the wallpaper that Classroom Manager saved before it first changed each selected user's desktop?\n\n" +
                "If that saved image is no longer available, the client uses the stock Windows wallpaper. " +
                "The restored image can therefore be different on each computer.",
                "Restore previous wallpaper", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        await _run(targets, "Restore previous wallpaper", (c, s) => s.CallAtAsync(Levels.User, Cmd.WallpaperReset),
            allowRetry: true);
    }

    public async Task ScreenSaverAsync()
    {
        var targets = _selected();
        if (targets.Count == 0) return;
        if (!_supports(targets, Cmd.ScreenSaverSet, "Screen saver management")) return;

        const string turnOff = AppearanceMessages.TurnOff;
        const string installFile = AppearanceMessages.InstallFile;
        const string restorePrevious = AppearanceMessages.RestorePrevious;
        var available = new Dictionary<string, string>(StringComparer.Ordinal);

        var dlg = AppearancePresentation.Current.CreateForm(_owner, targets, available,
            async () => await Task.WhenAll(targets.Select(LoadScreenSaverAsync)),
            BuiltInModules.Enabled(_settings, BuiltInModules.Appearance));
        if (!dlg.Ask("Review")) return;

        var choice = dlg.S("choice");
        var restore = choice == restorePrevious;
        var off = choice == turnOff;
        ScreenSaverFile? file = null;
        var seconds = 0;
        if (!restore && !off)
        {
            if (!ScreenSaverInventory.TryParseMinutes(dlg.S("minutes"), out seconds))
            {
                MessageBox.Show(_owner, "Enter the idle time as a whole number of minutes from 1 to 9999.", "Screen saver",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (choice == installFile)
            {
                if (dlg.S("file").Length == 0)
                {
                    MessageBox.Show(_owner, "Choose the .scr file to install.", "Screen saver", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                file = ScreenSaverFile.Inspect(dlg.S("file"), out var problem);
                if (file is null)
                {
                    MessageBox.Show(_owner, problem, "Screen saver", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
        }

        // Windows counts the display-off time from the same idle moment as the screen saver, so the
        // time asked for here is added to the screen saver's wait. It only applies with a screen saver.
        int? displaySeconds = null;
        if (!restore && !off)
        {
            if (!DisplayPowerRules.TryParseMinutesShown(dlg.S("displayMinutes"), out var minutesShown))
            {
                MessageBox.Show(_owner, $"Enter the screen-off time as a whole number of minutes from 0 to {DisplayPowerRules.MaxMinutesShown}, or leave it empty.",
                    "Screen saver", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (minutesShown is { } shown) displaySeconds = DisplayPowerRules.AfterScreenSaver(seconds, shown);
            if (displaySeconds is not null && !_supports(targets, Cmd.PowerDisplaySet, "Changing when the screen turns off")) return;
        }

        var selectedName = file?.Name ?? (available.TryGetValue(choice, out var existing) ? existing : "");
        var secure = dlg.B("secure");
        var items = new List<BulkOperationReviewItem>
        {
            new("Action", restore ? "Restore the settings saved before Classroom Manager first changed them"
                : off ? "Turn the screen saver off"
                : file is not null ? $"Install and select {file.Name}"
                : $"Select {selectedName}"),
        };
        if (file is not null)
        {
            items.Add(new("File", $"{file.Path} ({file.Length:N0} bytes)"));
            items.Add(new("SHA-256", file.Sha256));
            items.Add(new("Signature", file.IsSigned ? $"{file.Signer} (not validated by the Host)" : "Not signed"));
        }
        if (!restore && !off)
        {
            items.Add(new("Starts after", $"{seconds / 60} idle minute(s)"));
            items.Add(new("On resume", secure ? "Show the sign-in screen" : "Return to the desktop"));
            items.Add(new("Screen while plugged in", displaySeconds is not { } total ? "Windows' setting is left as it is"
                : total == 0 ? "Never turns off"
                : $"Turns off {DisplayPowerRules.Describe(total)}, {(total - seconds) / 60} minute(s) after the screen saver starts"));
        }
        else if (restore)
            items.Add(new("Screen while plugged in", "The earlier turn-off time is restored where Classroom Manager changed it"));
        var targetText = OperationTargetSummary.Format(targets);
        var review = new BulkOperationReview(
            "Review screen saver change",
            targetText.Heading + "\n" + targetText.Detail,
            items,
            "Applies to the user signed in on each computer; computers without a signed-in user fail and can be retried. " +
            "Each client verifies the SHA-256 before installing an uploaded file.",
            file is null ? ""
                : file.IsSigned
                    ? "This file is a program that will run in each signed-in user's session whenever the computer is idle."
                    : "This file is an unsigned program that will run in each signed-in user's session whenever the computer is idle. Install it only if you built it or trust its source.",
            file is not null ? "Install and apply" : "Apply");
        if (new BulkOperationReviewWindow(_owner, review).Ask() != BulkOperationReviewDecision.Run) return;

        var title = restore ? "Restore previous screen saver"
            : off ? "Turn off screen saver"
            : $"Set screen saver {selectedName}";
        var request = new ScreenSaverChangeRequest(restore, off, selectedName, seconds, secure, file, displaySeconds);
        await _run(targets, title,
            (client, session) => new ClientAppearanceActions(client, session, MyStaging).ChangeScreenSaverAsync(request),
            allowRetry: true, warnings: review.Warning,
            undo: restore ? null : ("Restore previous screen saver", (client, session) =>
                new ClientAppearanceActions(client, session, MyStaging)
                    .ChangeScreenSaverAsync(new(true, false, "", 0, false, null, null))));
    }

    async Task<ScreenSaverRead> LoadScreenSaverAsync(ClientInfo client)
    {
        try
        {
            using var session = await ClientSession.OpenAsync(client, _settings);
            var data = await session.CallAtAsync(Levels.User, Cmd.ScreenSaverGet);
            return new(client, ScreenSaverState.FromJson(data), "");
        }
        catch (Exception ex) { return new(client, null, ex.Message); }
    }

    public void ManagedWallpapers()
    {
        var targets = _selected();
        if (targets.Count == 0) return;
        if (targets.Count != 1)
        {
            MessageBox.Show(_owner, "Select exactly one computer to inspect its managed wallpaper images.",
                "Managed wallpapers", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_supports(targets, Cmd.WallpaperManagedList, "Managed wallpaper inventory")) return;
        new ManagedWallpapersWindow(targets[0], _settings, _owner).ShowDialog();
    }

    public void RecentWallpapers()
    {
        var targets = _selected();
        if (targets.Count == 0 || !_supports(targets, Cmd.WallpaperRecentList, "Recent wallpapers")) return;
        new RecentWallpapersWindow(targets, _settings, _owner).Show();
    }

    public void WindowsThemes()
    {
        var targets = _selected();
        if (targets.Count == 0) return;
        if (targets.Count != 1)
        {
            MessageBox.Show(_owner, "Select exactly one computer because available Windows themes can differ by user and computer.",
                "Windows themes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_supports(targets, Cmd.WindowsThemesList, "Windows theme selection")) return;
        new WindowsThemesWindow(targets[0], _settings, _owner).ShowDialog();
    }

    public async Task WindowsPreferencesAsync()
    {
        var targets = _selected();
        if (targets.Count == 0) return;
        if (!_supports(targets, Cmd.WindowsPreferencesSet, "Windows appearance and language")) return;

        var themeChoices = new[] { "(unchanged)", "Light", "Dark", "Windows default (Light)" };
        const string accentUnchanged = "(unchanged)", accentDefault = "Windows default (reset)";
        var accentChoices = new[] { accentUnchanged, accentDefault }.Concat(AccentColours.Palette.Select(colour => colour.Name)).ToArray();
        var barChoices = new[] { "(unchanged)", "Show the colour", "Do not show the colour" };
        var dlg = new FormDialog("Windows appearance and language", _owner)
            .Target(targets)
            .Note("These settings apply to the signed-in user on each selected Windows 10/11 computer. " +
                  "A language change takes effect after that user signs out. Automatic language-pack download uses " +
                  "Windows 11 LanguagePackManagement; Windows 10 can apply an already-installed language, while missing packs require Settings or your deployment source.")
            .Combo("theme", "Windows color mode", themeChoices)
            .Combo("accent", "Accent colour (buttons, links, selections)", accentChoices)
            .Combo("bars", "Accent colour on title bars and window borders", barChoices)
            .Text("language", "Interface language tag (optional, for example en-US, lt-LT, uk-UA)")
            .Check("install", "Install the language first if Windows supports automatic download")
            .Check("resetLanguage", "Reset the interface-language override to the Windows default");
        if (!dlg.Ask("Apply")) return;

        var language = dlg.S("language");
        var resetLanguage = dlg.B("resetLanguage");
        if (language.Length > 0 && resetLanguage)
        {
            MessageBox.Show(_owner, "Enter a language or select reset, not both.", "Windows appearance and language",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var theme = dlg.S("theme") switch
        {
            "Light" => "light",
            "Dark" => "dark",
            "Windows default (Light)" => "default",
            _ => "unchanged",
        };
        var accentName = dlg.S("accent");
        var accent = accentName switch
        {
            accentUnchanged => AccentColours.Unchanged,
            accentDefault => AccentColours.Default,
            _ => "#" + AccentColours.Palette.First(colour => colour.Name == accentName).Rgb,
        };
        bool? titleBars = dlg.S("bars") switch { "Show the colour" => true, "Do not show the colour" => false, _ => null };
        bool changesAccent = accent != AccentColours.Unchanged || titleBars is not null;
        if (theme == "unchanged" && !changesAccent && language.Length == 0 && !resetLanguage) return;
        if (changesAccent)
        {
            var old = targets.Where(client => !AccentColours.Supported(client.Version)).ToList();
            if (old.Count > 0)
            {
                MessageBox.Show(_owner,
                    Loc.F($"{old.Count} selected computer(s) have a Client too old to change the accent colour (for example {old[0].Name}). Update the Client, or select other computers."),
                    "Windows appearance and language", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        if (dlg.B("install") && language.Length > 0 &&
            !_supports(targets, Cmd.LanguageInstall, "Automatic language installation")) return;

        var summary = new List<string>();
        if (theme != "unchanged") summary.Add(dlg.S("theme"));
        if (accent != AccentColours.Unchanged) summary.Add(Loc.F($"accent colour: {accentName}"));
        if (titleBars is not null) summary.Add(Loc.F($"accent colour on title bars: {dlg.S("bars")}"));
        if (language.Length > 0) summary.Add($"interface language {language}" + (dlg.B("install") ? " (install if missing)" : ""));
        if (resetLanguage) summary.Add("Windows default interface language");
        if (MessageBox.Show(_owner,
                $"Apply the following to {targets.Count} selected computer(s)?\n\n{string.Join("\n", summary)}\n\n" +
                "Users may need to sign out before all language and theme changes are visible.",
                "Confirm Windows preferences", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        var args = new JsonObject
        {
            ["theme"] = theme,
            ["language"] = language,
            ["resetLanguage"] = resetLanguage,
        };
        AccentColours.AddTo(args, accent, titleBars);
        await _run(targets, "Windows appearance and language",
            (client, session) => new ClientAppearanceActions(client, session, MyStaging)
                .ChangePreferencesAsync(language, dlg.B("install"), args));
    }

    public async Task MouseAsync()
    {
        var targets = _selected();
        if (targets.Count == 0) return;

        var dlg = new FormDialog("Mouse settings", _owner)
            .Target(targets)
            .Note("Leave a box empty to keep that setting unchanged.")
            .Text("speed", "Pointer speed (1-20, Windows default 10)")
            .Text("dbl", "Double-click time in ms (200-900, default 500)")
            .Text("wheel", "Scroll lines per wheel notch (default 3)")
            .Combo("swap", "Primary button", new[] { "(unchanged)", "Left", "Right (swapped)" })
            .Combo("prec", "Enhance pointer precision", new[] { "(unchanged)", "On", "Off" });
        if (!dlg.Ask("Apply")) return;

        var args = new JsonObject();
        if (int.TryParse(dlg.S("speed"), out var speed)) args["speed"] = speed;
        if (int.TryParse(dlg.S("dbl"), out var dbl)) args["doubleClickMs"] = dbl;
        if (int.TryParse(dlg.S("wheel"), out var wheel)) args["wheelLines"] = wheel;
        var swap = dlg.S("swap");
        if (swap.StartsWith("Left")) args["swapButtons"] = false;
        else if (swap.StartsWith("Right")) args["swapButtons"] = true;
        var prec = dlg.S("prec");
        if (prec == "On") args["precision"] = true;
        else if (prec == "Off") args["precision"] = false;

        if (args.Count == 0) return;
        await _run(targets, "Mouse settings", (c, s) => s.CallAtAsync(Levels.User, Cmd.MouseSet, args),
            allowRetry: true);
    }

    public async Task MouseResetAsync()
    {
        var targets = _selected();
        if (targets.Count == 0) return;
        await _run(targets, "Reset mouse", (c, s) => s.CallAtAsync(Levels.User, Cmd.MouseReset), allowRetry: true);
    }

    public async Task DesktopLayoutResetAsync()
    {
        var targets = _selected();
        if (targets.Count == 0 || !_supports(targets, Cmd.DesktopLayoutReset, "Reset desktop layout")) return;
        var dlg = new FormDialog("Reset desktop layout", _owner)
            .Target(targets)
            .Check("iconSize", "Reset desktop icons to medium size", true)
            .Check("arrangeIcons", "Auto-arrange desktop icons and snap them to the grid")
            .Check("taskbar", "Reset taskbar position/alignment to the Windows default")
            .Note("Applying these settings restarts Explorer for each signed-in user. Open File Explorer windows will close; applications and documents stay open.");
        if (!dlg.Ask("Reset")) return;
        if (!dlg.B("iconSize") && !dlg.B("arrangeIcons") && !dlg.B("taskbar"))
        {
            MessageBox.Show(_owner, "Choose at least one setting to reset.");
            return;
        }
        if (MessageBox.Show(_owner, $"Restart Explorer and reset the selected layout settings on {targets.Count} computer(s)?",
                "Confirm desktop layout reset", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

        await _run(targets, "Reset desktop layout", (client, session) =>
            session.CallAtAsync(Levels.User, Cmd.DesktopLayoutReset, new JsonObject
            {
                ["iconSize"] = dlg.B("iconSize"),
                ["arrangeIcons"] = dlg.B("arrangeIcons"),
                ["taskbar"] = dlg.B("taskbar"),
            }));
    }
}
