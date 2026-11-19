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
    async void Wallpaper_Click(object sender, RoutedEventArgs e) => await _appearance.WallpaperAsync();
    async void WallpaperReset_Click(object sender, RoutedEventArgs e) => await _appearance.WallpaperResetAsync();
    void AppearanceSettings_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0 || !EnsureFeatureSupported(targets, Cmd.AppearanceCatalog, "Appearance settings")) return;
        new AppearanceSettingsWindow(targets, _settings, this).Show();
    }

    async void ScreenSaver_Click(object sender, RoutedEventArgs e) => await _appearance.ScreenSaverAsync();
    void ManagedWallpapers_Click(object sender, RoutedEventArgs e) => _appearance.ManagedWallpapers();
    void RecentWallpapers_Click(object sender, RoutedEventArgs e) => _appearance.RecentWallpapers();
    void WindowsThemes_Click(object sender, RoutedEventArgs e) => _appearance.WindowsThemes();
    async void WindowsPreferences_Click(object sender, RoutedEventArgs e) => await _appearance.WindowsPreferencesAsync();
    async void Mouse_Click(object sender, RoutedEventArgs e) => await _appearance.MouseAsync();
    async void MouseReset_Click(object sender, RoutedEventArgs e) => await _appearance.MouseResetAsync();
    async void DesktopLayoutReset_Click(object sender, RoutedEventArgs e) => await _appearance.DesktopLayoutResetAsync();

    async void SignInUser_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        if (!EnsureFeatureSupported(targets, Cmd.UserSignIn, "Remote sign-in")) return;

        var dlg = new FormDialog("Sign in user", this)
            .Target(targets)
            .Note("Signs one account in on every selected computer that nobody is using, so the room is ready before a lesson. " +
                  "Each computer checks the password, restarts, and signs the account in once.")
            .Text("user", @"_Account (user, .\user, DOMAIN\user, or user@domain)")
            .Text("password", "_Password", password: true)
            .Note("Computers where someone is signed in refuse and are not restarted. The password is sent over the encrypted connection, " +
                  "kept by Windows as a protected secret until the sign-in, and is not saved or logged by the Host.");
        if (!dlg.Ask("Review")) return;

        var account = SignInAccount.Parse(dlg.S("user"));
        if (account is null)
        {
            MessageBox.Show(this, @"Enter the account as user, .\user, DOMAIN\user, or user@domain.", "Sign in user",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Not trimmed: a password may begin or end with a space.
        var password = dlg.Raw("password");
        if (password.Length == 0)
        {
            MessageBox.Show(this, "Enter the account's password. Windows does not sign in an account with an empty password automatically.",
                "Sign in user", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targetText = OperationTargetSummary.Format(targets);
        var review = new BulkOperationReview(
            "Review remote sign-in",
            targetText.Heading + "\n" + targetText.Detail,
            [
                new("Account", account.IsLocal ? $"{account.User} (an account of each computer)" : account.Display),
                new("Action", "Check the password, restart, and sign in once"),
                new("Skipped", "Computers where someone is already signed in"),
            ],
            "Each computer removes the stored password after the sign-in, or after ten minutes if it did not happen. Failed computers are not retried automatically.",
            "Every selected computer that accepts will restart, and whoever is at it afterwards has this account's session. A wrong password counts as one failed sign-in attempt for the account on each computer.",
            "Restart and sign in");
        if (new BulkOperationReviewWindow(this, review).Ask() != BulkOperationReviewDecision.Run) return;

        // The password travels only in the request; the title, log and operation details never contain it.
        await RunOnAsync(targets, $"Sign in {account.Display}", async (client, session) =>
        {
            await session.CallAsync(Cmd.UserSignIn, new JsonObject { ["user"] = dlg.S("user"), ["password"] = password });
            client.Status = "Restarting to sign in";
        }, warnings: review.Warning);
    }

    void AppAccounts_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0 || !EnsureFeatureSupported(targets, Cmd.AppAccountsList, "App accounts")) return;
        new AppAccountsWindow(targets, _settings, this).Show();
    }

    async void RecycleBinEmpty_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0 || !EnsureFeatureSupported(targets, Cmd.RecycleBinNameReset, "Recycle Bin")) return;
        var dlg = new FormDialog("Recycle Bin", this)
            .Target(targets)
            .Check("name", "Reset the name of the Recycle Bin to the Windows default", true)
            .Check("empty", "Empty the Recycle Bin (what is in it cannot be restored)")
            .Note("Both apply to the user signed in on each computer. A renamed Recycle Bin gets the name Windows gives it in that user's language.");
        if (!dlg.Ask("Apply")) return;
        bool resetName = dlg.B("name"), empty = dlg.B("empty");
        if (!resetName && !empty)
        {
            MessageBox.Show(this, "Choose at least one of the two.");
            return;
        }
        if (empty && MessageBox.Show(this, $"Empty the Recycle Bin of the signed-in user on {targets.Count} computer(s)?\n\n" +
                "Everything in it is deleted for good and cannot be restored.",
                "Empty Recycle Bin", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        await RunOnAsync(targets, resetName && empty ? "Reset and empty Recycle Bin" : empty ? "Empty Recycle Bin" : "Reset Recycle Bin name",
            async (c, s) =>
            {
                if (resetName) Log($"  {c.Name}: {RecycleBinRules.DescribeName(await s.CallAtAsync(Levels.User, Cmd.RecycleBinNameReset))}");
                if (empty) Log($"  {c.Name}: {RecycleBinRules.Describe(await s.CallAtAsync(Levels.User, Cmd.RecycleBinEmpty))}");
            });
    }

    // ---- actions shared by buttons and schedules ---------------------------------------------------

    /// <summary>The scheduled "Apply an Appearance preset": the saved preset named in the schedule's text, on each computer, as ClassroomCli appearance apply does.</summary>
    async Task DoAppearancePresetAsync(List<ClientInfo> targets, string presetName)
    {
        if (!BuiltInModules.Enabled(_settings, BuiltInModules.Appearance)) { Log("  " + BuiltInModules.Disabled); return; }
        await RunOnAsync(targets, "Apply Appearance preset", async (c, s) =>
        {
            try { Log($"  {c.Name}: {await CliAppearance.ApplyAsync(c, s, _settings, presetName, CancellationToken.None)}"); }
            catch (Exception ex) when (ex is CliUsageException or InvalidOperationException) { Log($"  {c.Name}: {ex.Message}"); }
        }, allowRetry: false);
    }

    // ---- helpers ------------------------------------------------------------------------------------
}
