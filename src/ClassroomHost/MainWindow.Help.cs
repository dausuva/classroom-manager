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
    readonly AdminMessages _adminMessages = new();

    /// <summary>A teacher leaves a note for the lab's administrator; it waits on this computer for whoever opens Classroom Manager next.</summary>
    void MessageToAdministrator_Click(object sender, RoutedEventArgs e)
    {
        const string caption = "Message to administrator";
        var selected = SelectedRaw();
        var names = string.Join(", ", selected.Take(8).Select(client => client.Name));
        var dlg = new FormDialog(caption, this)
            .Note("For the person who looks after this lab: a computer that needs attention, a website to unblock, a program that is missing. " +
                  "The note stays on this computer and is shown to anyone who opens Messages from teachers... here, under any Windows account. It is not sent anywhere.")
            .Text("text", "_Message", selected.Count > 0 ? Loc.T("About ") + names + ": " : "", multiline: true);
        if (!dlg.Ask("Leave message")) return;
        var problem = _adminMessages.Leave(dlg.S("text"), Environment.UserName, HostBuildInfo.Version);
        if (problem.Length > 0)
        {
            MessageBox.Show(this, problem, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Log("A message was left for the administrator.");
        MessageBox.Show(this, "The message was left for the administrator.", caption, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>The administrator reads the notes that wait on this computer and removes the ones dealt with.</summary>
    void MessagesFromTeachers_Click(object sender, RoutedEventArgs e)
    {
        const string caption = "Messages from teachers";
        var waiting = _adminMessages.Waiting();
        if (waiting.Count == 0)
        {
            MessageBox.Show(this, "No messages are waiting on this computer.", caption, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var rows = waiting.Select(message => message.Heading + BrowserSites.RowSeparator + message.Text.ReplaceLineEndings(" ")).ToList();
        var dlg = new FormDialog(caption, this)
            .Note($"{waiting.Count} message(s) were left on this computer, oldest first.")
            .MultiSelect("done", "Tick the ones that are dealt with", rows, filter: waiting.Count > 8)
            .Note("Ticked messages are removed for good. The others stay for next time.");
        if (!dlg.Ask("Remove ticked")) return;
        var ticked = dlg.S("done").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        int removed = _adminMessages.Remove(waiting.Where((_, index) => ticked.Contains(rows[index])));
        if (removed > 0) Log($"{removed} message(s) from teachers were removed.");
    }

    void AnnounceWaitingMessages()
    {
        var waiting = _adminMessages.Waiting().Count;
        if (waiting > 0) Log($"{waiting} message(s) from teachers are waiting: Settings > Messages from teachers...");
    }

    /// <summary>Once a day, and only when switched on: is a newer version published? Says so in the log and the title; installs nothing.</summary>
    async Task LookForNewerVersionAsync(CancellationToken cancellationToken)
    {
        if (!FirstLaunchNotice.OffersUpdateCheck(ClassroomManager.Shared.PackageIdentity.IsPackaged) || !_settings.CheckForUpdates || !UpdateNotice.Due(_settings.LastUpdateCheckUtc, DateTime.UtcNow)) return;
        _settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("O");
        _settings.Save();
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (await UpdateNotice.CheckAsync(http, HostBuildInfo.Version, cancellationToken) is not { } release) return;
        cancellationToken.ThrowIfCancellationRequested();
        Log($"A newer version of Classroom Manager is available: {release.Version}. See {release.Page}");
        Title += $" - version {release.Version} is available";
    }

    /// <summary>The welcome notice of a new Host, shown once; asks nothing but whether to look daily for a newer version (not for a packaged Host).</summary>
    void ShowFirstLaunchNotice()
    {
        var packaged = ClassroomManager.Shared.PackageIdentity.IsPackaged;
        var dialog = new FormDialog("Welcome to Classroom Manager", this);
        foreach (var paragraph in FirstLaunchNotice.Paragraphs(HostSettings.DataFolder, packaged)) dialog.Note(paragraph);
        dialog.Button("Privacy information", FirstLaunchNotice.PrivacyPage, () =>
        {
            try { Process.Start(new ProcessStartInfo(FirstLaunchNotice.PrivacyPage) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        });
        if (FirstLaunchNotice.OffersUpdateCheck(packaged))
            dialog.Check("updates", "Look once a day whether a newer version is published (contacts github.com; nothing is downloaded)", _settings.CheckForUpdates);
        var accepted = dialog.Ask("Continue");
        if (accepted && FirstLaunchNotice.OffersUpdateCheck(packaged)) _settings.CheckForUpdates = dialog.B("updates");
        _settings.FirstLaunchNoticeShown = true;
        _settings.Save();
    }

    void InstallHere_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelfInstallPaths.ForThisUser();
        if (Environment.ProcessPath is not { } source || HostSelfInstall.DetectHere() != SelfInstallState.Offered) { InstallHereButton.Visibility = Visibility.Collapsed; return; }
        if (MessageBox.Show(this, $"Install Classroom Manager Host for your account in {paths.InstallFolder}? It is added to the Start menu and to Settings > Apps. Your settings stay as they are.",
                "Install on this computer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
        try { HostSelfInstall.Install(source, HostBuildInfo.Version, paths); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(this, "It was not installed: " + ex.Message, "Install on this computer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this, "Installed. Start the installed copy now and close this one?", "Install on this computer", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes) == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo(paths.ProgramPath) { UseShellExecute = true });
            Close();
        }
        else InstallHereButton.Visibility = Visibility.Collapsed;
    }

    void Help_Click(object sender, RoutedEventArgs e)
    {
        var problem = HelpGuide.Open(Loc.Current.Language);
        if (problem.Length > 0)
            MessageBox.Show(this, "The guide could not be opened: " + problem, "Help", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    async void SetupAssistant_Click(object sender, RoutedEventArgs e) => await RunSetupAssistantAsync(firstStart: false);

    /// <summary>
    /// The first steps of a new Host, one after the other: take a key (from a teacher settings file,
    /// a configuration file, or typed in), find the computers on the network, and put the ones found
    /// into a room so they are watched. Every step can be skipped and done later from the tabs.
    /// </summary>
    async Task RunSetupAssistantAsync(bool firstStart)
    {
        const string caption = "Setup assistant";
        const string teacherFile = "I have a teacher settings file from my administrator", typedKey = "I have a key to type in",
                     configuration = "I have a configuration file exported from another Classroom Manager", keep = "Keep the key I have";
        var choices = _settings.HasHostKeys ? new[] { keep, teacherFile, typedKey, configuration } : new[] { teacherFile, typedKey, configuration };
        var dlg = new FormDialog(caption, this)
            .Note((firstStart ? "Welcome to Classroom Manager. " : "") +
                  "Three steps make it ready: a key the student computers accept, the list of computers, and a room to put them in. " +
                  "Press F1 at any time for the guide with pictures.")
            .Combo("how", "Step 1 of 3 — the _key", choices)
            .Note("Your administrator hands out the key: usually as a file, which also brings the computers and their rooms.");
        if (!dlg.Ask("Continue")) return;

        switch (dlg.S("how"))
        {
            case teacherFile: ImportTeacherSettings_Click(this, new RoutedEventArgs()); break;
            case configuration: ImportConfiguration_Click(this, new RoutedEventArgs()); return;   // restarts with the imported settings
            case typedKey: Settings_Click(this, new RoutedEventArgs()); break;
        }
        if (!_settings.HasHostKeys)
        {
            MessageBox.Show(this, "No key was stored, so the computers cannot be reached yet. Open the assistant again from Settings when you have one.",
                caption, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Step 2: a settings file usually brought the computers; otherwise look for them.
        if (_clients.All(client => client.Room.Length == 0))
        {
            if (MessageBox.Show(this, "Step 2 of 3 — find the computers.\n\nLook for Classroom Client computers on this network now? It takes a few seconds.",
                    caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await _windowWork.RunAsync(ScanAsync);
        }

        // Step 3: computers without a room are not watched.
        var unassigned = _clients.Where(client => client.Room.Length == 0).ToList();
        if (unassigned.Count == 0)
        {
            MessageBox.Show(this,
                _clients.Count == 0 ? "No computers were found. Check that they are on and on this network, then use Computers > Scan network, or Add computer... to type an address."
                                    : $"Ready: {_clients.Count} computer(s) are in rooms and are checked every 30 seconds. Tick computers in the list, then choose an action.",
                caption, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var room = new FormDialog(caption, this)
            .Note($"Step 3 of 3 — {unassigned.Count} computer(s) are not in a room yet: " +
                  string.Join(", ", unassigned.Take(12).Select(client => client.Name)) + (unassigned.Count > 12 ? ", ..." : "") + ".")
            .Choice("room", "_Room for them (pick one, or type a new name such as the number of the classroom)",
                Rooms().Select(name => (name, "")).ToList(), Rooms().FirstOrDefault() ?? "")
            .Require("room", "Enter a room name.")
            .Note("Only computers in a room are checked every 30 seconds. Computers of another room can be moved later with Assign to room...");
        if (!room.Ask("Assign") || room.S("room").Length == 0) return;
        foreach (var client in unassigned) client.Room = room.S("room");
        SaveComputers();
        RefreshRooms();
        Log($"Setup assistant: {unassigned.Count} computer(s) assigned to room {room.S("room")}.");
        await CheckAllAsync();
    }

    void About_Click(object sender, RoutedEventArgs e) => new AboutWindow(this, _clients.Count).ShowDialog();

    void Licenses_Click(object sender, RoutedEventArgs e) => new OpenSourceLicensesWindow(this).ShowDialog();

    void Feedback_Click(object sender, RoutedEventArgs e) => new FeedbackManagerWindow(this).ShowDialog();

    void CrashLogs_Click(object sender, RoutedEventArgs e) => new CrashLogWindow(this).ShowDialog();

    void FavoriteCommands_Click(object sender, RoutedEventArgs e)
    {
        var window = new FavoriteCommandsWindow(this, _settings.FavoriteCommands);
        if (window.ShowDialog() != true) return;
        _settings.FavoriteCommands = window.Commands;
        _settings.Save();
    }
}
