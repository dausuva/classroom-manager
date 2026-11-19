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
    /// <summary>
    /// Creates a Teacher key on the selected computers and saves it, with those computers and
    /// optionally this Host's classroom presets, in one file a teacher imports on their own Host.
    /// </summary>
    async void ExportTeacherSettings_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0 || !EnsureFeatureSupported(targets, Cmd.AccessKeysSet, "Export teacher settings")) return;

        var dlg = new FormDialog("Export teacher settings", this)
            .Target(targets)
            .Note("A new Teacher key is created on the selected computers and saved in a file together with their names, addresses and rooms. " +
                  "The teacher chooses Settings > Import teacher settings... in their own Classroom Manager and can start working. " +
                  "The file never contains your own key.")
            .Text("name", "Key _name (who holds it, for example “Room 12 teacher” or a person's name)", "Teacher")
            .Check("presets", "Include my classroom _presets: Exam mode lists, block lists, saved websites and programs, quick actions and the commands shown in Teacher mode", true)
            .Text("password", "_Password for the file (recommended; tell it to the teacher separately)", password: true)
            .Text("again", "Password _again", password: true)
            .Note("A key with the same name that is already on those computers is replaced, so an earlier file with that name stops working there. " +
                  "Remove the key again with Access keys... to take the teacher's access away.");
        if (!dlg.Ask("Export...")) return;

        var name = dlg.S("name");
        var password = dlg.Raw("password");
        if (TeacherExportPlan.Problem(name, password, dlg.Raw("again")) is { } problem)
        {
            MessageBox.Show(this, problem, TeacherExportPlan.Caption, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (TeacherExportPlan.NeedsNoPasswordConfirmation(password) && MessageBox.Show(this,
                "Without a password, anyone who gets the file can use the Teacher key on these computers.\n\nExport it without a password?",
                "Export teacher settings", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        // Asked before the key is created, so cancelling here leaves the computers untouched.
        var save = new SaveFileDialog { FileName = TeacherSettingsFile.DefaultFileName, Filter = "Teacher settings (*.json)|*.json" };
        if (save.ShowDialog(this) != true) return;

        var key = SharedKey.Generate();
        var accepted = new ConcurrentBag<ClientInfo>();
        await RunOnAsync(targets, TeacherExportPlan.Title(name), (_, session) =>
                session.CallAsync(Cmd.AccessKeysSet, TeacherExportPlan.Arguments(name, key)),
            targetFinished: (client, error) => { if (error.Length == 0) accepted.Add(client); });
        if (accepted.IsEmpty)
        {
            MessageBox.Show(this, "No computer accepted the new key, so no file was written. See Operations for the reasons.",
                "Export teacher settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var settings = TeacherSettingsFile.Create(_settings, TeacherExportPlan.Computers(targets), name, key, dlg.B("presets"));
            System.IO.File.WriteAllText(save.FileName, TeacherSettingsFile.Write(settings, password));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, TeacherExportPlan.WriteFailed(name, ex.Message),
                "Export teacher settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Log($"Exported teacher settings with Teacher key '{name}' for {targets.Count} computer(s) to {save.FileName}");
        MessageBox.Show(this, TeacherExportPlan.Summary(save.FileName, name, accepted.Count, targets.Count, password.Length > 0),
            "Export teacher settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Takes the key, computers and presets from a file an administrator exported with Export teacher settings.</summary>
    async void ImportTeacherSettings_Click(object sender, RoutedEventArgs e)
    {
        const string caption = "Import teacher settings";
        var open = new OpenFileDialog { Filter = "Teacher settings (*.json)|*.json|All files (*.*)|*.*" };
        if (open.ShowDialog(this) != true) return;

        TeacherSettings teacher;
        try
        {
            var text = System.IO.File.ReadAllText(open.FileName);
            string? password = null;
            if (TeacherSettingsFile.IsProtected(text))
            {
                var ask = new FormDialog(caption, this)
                    .Note("This file is protected. Type the password the administrator gave you.")
                    .Text("password", "_Password", password: true);
                if (!ask.Ask("Open")) return;
                password = ask.Raw("password");
            }
            teacher = TeacherSettingsFile.Read(text, password);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new FormDialog(caption, this).Note(TeacherSettingsFile.Describe(teacher))
            .Note("The key is stored for your Windows account and the connection settings are taken from the file. Keys and computers already here are kept.");
        if (teacher.Computers.Count > 0) dlg.Check("computers", "Add the _computers and their rooms", true);
        if (teacher.Presets is not null)
        {
            dlg.Check("presets", "Add the classroom _presets (Exam mode lists, block lists, saved websites and programs)", true);
            dlg.Check("interface", "Use the administrator's _toolbar: Teacher mode, visible commands and quick actions (replaces yours)", _clients.Count == 0);
        }
        if (!dlg.Ask("Import")) return;

        bool computers = teacher.Computers.Count > 0 && dlg.B("computers");
        var changes = TeacherSettingsFile.Apply(teacher, _settings, teacher.Presets is not null && dlg.B("presets"), teacher.Presets is not null && dlg.B("interface"));
        if (computers)
        {
            int added = MergeComputers(teacher.Computers);
            changes.Add($"{teacher.Computers.Count} computer(s) in the file, {added} new.");
        }
        SaveComputers();
        foreach (var client in _clients) client.KeyName = _settings.KeyNameFromFingerprint(client.KeyId);
        RefreshRooms();
        ApplyHostMode();
        Log($"Imported teacher settings from {open.FileName}: {string.Join(" ", changes)}");
        MessageBox.Show(this, string.Join("\n", changes), caption, MessageBoxButton.OK, MessageBoxImage.Information);
        await CheckAllAsync();
    }

    /// <summary>Saves every setting and key of this Host in one password-protected file.</summary>
    void ExportConfiguration_Click(object sender, RoutedEventArgs e)
    {
        const string caption = "Export configuration";
        var dlg = new FormDialog(caption, this)
            .Note("Saves everything this Host knows in one file: the computers and rooms, every key (including the lab's own Admin key), " +
                  "schedules, Exam mode and block lists, saved websites and programs, and the interface choices. " +
                  "Use it as a backup kept somewhere else, or to set up this Host on another computer or Windows account.")
            .Text("password", $"_Password (at least {HostConfigurationFile.MinimumPasswordLength} characters)", password: true)
            .Text("again", "Password _again", password: true)
            .Note("Whoever has the file and the password controls every computer these keys reach. Without the password the file cannot be opened by anyone, you included.");
        if (!dlg.Ask("Export...")) return;
        var password = dlg.Raw("password");
        if (!HostConfigurationFile.PasswordIsAcceptable(password, out var reason) || password != dlg.Raw("again"))
        {
            MessageBox.Show(this, reason.Length > 0 ? reason : "The two passwords are not the same.", caption, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var save = new SaveFileDialog { FileName = HostConfigurationFile.DefaultFileName, Filter = "Classroom Manager configuration (*.cmconfig)|*.cmconfig" };
        if (save.ShowDialog(this) != true) return;
        try
        {
            SaveComputers();
            System.IO.File.WriteAllText(save.FileName, HostConfigurationFile.Write(_settings, password, HostBuildInfo.Version));
            Log($"Exported the Host configuration ({_settings.Computers.Count} computer(s), {_settings.CurrentHostKeys().Count()} key(s)) to {save.FileName}");
            MessageBox.Show(this, $"Saved {save.FileName}\n\nKeep the password somewhere else than the file.", caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, "The configuration could not be exported: " + ex.Message, caption, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>Replaces this Host's settings and keys with an exported configuration, then restarts the Host.</summary>
    void ImportConfiguration_Click(object sender, RoutedEventArgs e)
    {
        const string caption = "Import configuration";
        var open = new OpenFileDialog { Filter = "Classroom Manager configuration (*.cmconfig)|*.cmconfig|All files (*.*)|*.*" };
        if (open.ShowDialog(this) != true) return;
        try
        {
            var text = System.IO.File.ReadAllText(open.FileName);
            if (!HostConfigurationFile.IsConfiguration(text))
            {
                MessageBox.Show(this, "This is not a configuration file exported from Classroom Manager. A teacher settings file is imported with Import teacher settings...",
                    caption, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var ask = new FormDialog(caption, this).Text("password", "_Password of the file", password: true);
            if (!ask.Ask("Open")) return;
            var (imported, info) = HostConfigurationFile.Read(text, ask.Raw("password"));

            var dlg = new FormDialog(caption, this)
                .Note(info.Describe())
                .Note($"This replaces everything this Host has now ({_clients.Count} computer(s), {_settings.CurrentHostKeys().Count()} key(s)). " +
                      "What it has now is first kept under Settings backups..., so it can be put back. Classroom Manager then restarts.")
                .Check("second", "The Host this file came from stays in use too: give this one its own identity and do not run _schedules here", false);
            if (!dlg.Ask("Replace and restart")) return;

            HostConfigurationFile.Install(HostSettings.PathOnDisk, imported, dlg.B("second"));
            RestartWithSettingsFromDisk();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, caption, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>Lists the automatic copies of the settings and keys and puts a chosen one back.</summary>
    void SettingsBackups_Click(object sender, RoutedEventArgs e)
    {
        const string caption = "Settings backups";
        var backups = HostBackups.List(HostSettings.PathOnDisk);
        if (backups.Count == 0)
        {
            MessageBox.Show(this, "There are no backups yet. One is made each time Classroom Manager starts with changed settings, and before settings are imported, restored or emptied.",
                caption, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var labels = backups.Select(backup => backup.Label).ToArray();
        var dlg = new FormDialog(caption, this)
            .Note($"Classroom Manager keeps the last {HostBackups.Keep} copies of its settings and keys in {HostBackups.FolderFor(HostSettings.PathOnDisk)}. " +
                  "A copy can only be used by the Windows account that made it.")
            .Combo("backup", "_Backup to put back", labels)
            .Note($"Now: {_clients.Count} computer(s), {_settings.CurrentHostKeys().Count()} key(s). Putting a backup back replaces that; what you have now becomes a backup itself. Classroom Manager then restarts.");
        if (!dlg.Ask("Restore and restart")) return;
        var chosen = backups[Math.Max(0, Array.IndexOf(labels, dlg.S("backup")))];
        if (MessageBox.Show(this, $"Put back the settings and keys from {chosen.TakenUtc.ToLocalTime():yyyy-MM-dd HH:mm} ({chosen.Computers} computer(s))?",
                caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            SaveComputers();   // so the copy made of "now" is complete
            HostBackups.Restore(HostSettings.PathOnDisk, chosen);
            RestartWithSettingsFromDisk();
        }
        catch (Exception ex) { MessageBox.Show(this, "The backup could not be put back: " + ex.Message, caption, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>Lists the languages, takes in a translation file a user made, and saves the template to translate from.</summary>
    void Languages_Click(object sender, RoutedEventArgs e)
    {
        const string caption = "Languages";
        const string import = "Add or replace a language from a translation file (.po)", template = "Save the English texts as a template to translate (.pot)",
                     current = "Save the Lithuanian translation as an example (.po)", folder = "Open the folder with my translation files";
        var languages = Loc.Available(UiTranslator.LanguagesFolder);
        var dlg = new FormDialog(caption, this)
            .Note("Classroom Manager comes in English and Lithuanian. Choose the language under Interface settings...\n\n" +
                  "Now available:\n" + string.Join("\n", languages.Select(language => $"• {language.Name} ({language.Language}){(language.File is null ? "" : " - your file")}")))
            .Combo("action", "_Action", [import, template, current, folder])
            .Note("To add a language: save the template, translate it with a PO editor such as Poedit (free) or any text editor, set its \"Language\" line " +
                  "to the language code (for example pl-PL), and add the file here. A file for English or Lithuanian replaces the built-in texts. " +
                  "A text you leave untranslated is shown in English.");
        if (!dlg.Ask("Continue")) return;
        try
        {
            var action = dlg.S("action");
            if (action == folder)
            {
                System.IO.Directory.CreateDirectory(UiTranslator.LanguagesFolder);
                Process.Start(new ProcessStartInfo(UiTranslator.LanguagesFolder) { UseShellExecute = true });
            }
            else if (action == template || action == current)
            {
                var lithuanian = action == current;
                var save = new SaveFileDialog { FileName = lithuanian ? "lt-LT.po" : "classroom-manager.pot", Filter = lithuanian ? "Translation (*.po)|*.po" : "Translation template (*.pot)|*.pot" };
                if (save.ShowDialog(this) != true) return;
                System.IO.File.WriteAllText(save.FileName, LanguageFiles.BuiltIn(template: !lithuanian));
                MessageBox.Show(this, $"Saved {save.FileName}", caption, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                var open = new OpenFileDialog { Filter = "Translation (*.po)|*.po|All files (*.*)|*.*" };
                if (open.ShowDialog(this) != true) return;
                var added = LanguageFiles.Import(open.FileName, UiTranslator.LanguagesFolder);
                Log($"Added the language {added.Name} ({added.Language}) with {added.Translated} of {added.Total} texts translated.");
                MessageBox.Show(this,
                    $"{added.Name} ({added.Language}) was added: {added.Translated} of {added.Total} texts are translated" +
                    (added.Skipped > 0 ? $", {added.Skipped} were left in English because they lose or add a placeholder such as {{0}}" : "") +
                    ".\n\nChoose it under Interface settings...",
                    caption, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, caption, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>Starts a fresh Host with the settings as they are saved now and closes this one (a new language applies to every window from the start).</summary>
    void RestartToApplySettings()
    {
        _settings.Save();
        try
        {
            if (Environment.ProcessPath is { } path)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = false })?.Dispose();
            Application.Current.Shutdown();
        }
        catch (Exception ex) { MessageBox.Show(this, "Start Classroom Manager again by hand: " + ex.Message, "Classroom Manager", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    /// <summary>
    /// The files on disk were just replaced, while this window still holds the old settings: stop
    /// every further save, start a fresh Host that reads the new files, and close this one.
    /// </summary>
    void RestartWithSettingsFromDisk()
    {
        HostSettings.SavesSuspended = true;
        try
        {
            if (Environment.ProcessPath is { } path)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The settings were replaced. Start Classroom Manager again by hand: " + ex.Message, "Classroom Manager", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        Application.Current.Shutdown();
    }

    static readonly string[] ThemeChoices = { "System", "Light", "Dark" };
    static readonly string[] ButtonDisplayChoices = { "Icons and names", "Icons only", "Names only" };

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var subnetChoices = HostSettingsEditor.SubnetChoices(
            Discovery.ConnectedSubnets().Select(subnet => (subnet.DisplayName, subnet.Key)), _settings.DiscoverySubnet);
        var dialog = HostSettingsDialog.Build(this, _settings, subnetChoices);
        if (!dialog.Ask("Save")) return;

        var input = HostSettingsDialog.Read(dialog);
        if (HostSettingsEditor.Validate(_settings, input) is { } problem)
        {
            MessageBox.Show(this, problem);
            return;
        }

        BuiltInModules.ReadSettings(dialog, _settings);
        var applied = HostSettingsEditor.Apply(_settings, input, subnetChoices, new SettingsEffects(this));
        if (applied.LogMessage is not null) Log(applied.LogMessage);
        if (applied.Warning is not null)
            MessageBox.Show(this, applied.Warning, "Run schedules when the Host is closed", MessageBoxButton.OK, MessageBoxImage.Warning);
        _settings.Save();
        foreach (var client in _clients) client.KeyName = _settings.KeyNameFromFingerprint(client.KeyId);

        ApplyHostMode();

        ModuleUiPolicy.Refresh(_settings);
        RefreshNetworkScope();
        RefreshRooms();
        _view.Refresh();
        UpdateCollectionState();
        UpdateSummary();
    }

    /// <summary>The effects of saving Settings that reach outside the settings object: Task Scheduler, the crash log and the activity log.</summary>
    sealed class SettingsEffects(MainWindow window) : IHostSettingsEffects
    {
        public string? ApplyBackgroundSchedules(bool whenClosed) =>
            BackgroundSchedules.Apply(whenClosed, Environment.UserName, Environment.ProcessPath ?? "");

        public void SetCrashLogging(bool enabled) => CrashLog.SetEnabled(enabled);

        public void RemoveOldActivityLogs(int keepDays) => window._logFile.RemoveOld(DateTime.Now, keepDays);
    }

    void ExamModeSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FormDialog("Exam mode settings", this)
            .Note("These websites and programs are added as a separate exam overlay. Turning Exam mode off removes only the overlay and preserves the client's normal block lists.")
            .Text("domains", "Blocked websites (one per line or comma-separated)",
                string.Join(Environment.NewLine, _settings.ExamModeDomains), multiline: true)
            .Text("apps", "Blocked apps/processes (one per line or comma-separated)",
                string.Join(Environment.NewLine, _settings.ExamModeApps), multiline: true)
            .Check("muteAudio", "Mute speakers and microphones", _settings.ExamModeMuteAudio)
            .Check("disableCamera", "Turn cameras off and close the Camera app", _settings.ExamModeDisableCamera)
            .Check("blockUsbStorage", "Block USB drives, phones and other removable storage", _settings.ExamModeBlockUsbStorage)
            .Note("Device controls apply to the whole computer and are undone when Exam mode is turned off. They need Client protocol 1.8. " +
                  "Cameras and USB drives are disabled as in Device Manager, including ones plugged in during the exam, and enabled again afterwards.");
        if (!dialog.Ask("Save")) return;
        _settings.ExamModeDomains = SplitList(dialog.S("domains"), normalizeDomain: true);
        _settings.ExamModeApps = SplitList(dialog.S("apps"), normalizeDomain: false);
        _settings.ExamModeMuteAudio = dialog.B("muteAudio");
        _settings.ExamModeDisableCamera = dialog.B("disableCamera");
        _settings.ExamModeBlockUsbStorage = dialog.B("blockUsbStorage");
        _settings.Save();
        if (!_settings.ExamModeEnabled) return;
        foreach (var client in _clients) client.ExamModeApplied = false;
        _ = ApplyExamModeAsync(true, reapply: true);
    }

    void InterfaceSettings_Click(object sender, RoutedEventArgs e)
    {
        var quickActionLabels = QuickActionCatalog.All.Select(action => action.Label).ToList();
        var selectedQuickActionLabels = QuickActionCatalog.All
            .Where(action => _settings.QuickActions.Contains(action.Id, StringComparer.OrdinalIgnoreCase))
            .Select(action => action.Label).ToList();
        var teacherGroups = TeacherCommandCatalog.All.Select(command => command.Group).Distinct().ToList();
        // "System" follows Windows; the rest are the built-in languages and the files under Settings > Languages...
        var languages = InterfaceSettingsPlan.Languages(_settings.Language, Loc.Available(UiTranslator.LanguagesFolder));
        var dlg = new FormDialog("Interface settings", this)
            .Combo("theme", "Appearance", ThemeChoices, InterfaceSettingsPlan.ThemeIndex(ThemeChoices, _settings.Theme))
            .Combo("buttons", "Toolbar buttons", ButtonDisplayChoices, ButtonDisplayIndex(_settings.ButtonDisplay))
            .Combo("language", "Language", languages.Labels, languages.IndexOf(_settings.Language))
            .Combo("hostMode", "Host interface mode", [HostModes.Teacher, HostModes.Admin],
                _settings.HostMode == HostModes.Admin ? 1 : 0)
            .MultiSelect("quickActions", "Quick actions for both Admin and Teacher modes", quickActionLabels, selectedQuickActionLabels)
            .Text("quickMessage", "Default quick-action lock/message text", _settings.QuickActionMessage)
            .Check("explanations", "Always show the e_xplanation at the top of a window (otherwise it folds behind an info icon after the first 3 times)",
                _settings.AlwaysShowExplanations)
            .Button("Arrange _toolbar...", "Arrange toolbar — move actions between each tab's top row and its \"More\" part, or hide the ones you do not use.",
                () => ArrangeToolbar_Click(this, new RoutedEventArgs()))
            .Button("_Reset toolbar", "Reset toolbar — put every tab back as Classroom Manager lays it out.", ResetToolbar)
            .Note("Arrange toolbar and Interface settings are always shown, in both modes. Ctrl+Shift+T opens Arrange toolbar from anywhere; " +
                  "\"ClassroomCli toolbar-reset\" resets the toolbar while Classroom Host is closed.");
        for (var i = 0; i < teacherGroups.Count; i++)
        {
            var group = teacherGroups[i];
            // Actions a Teacher key is refused are not offered: the computers would turn them down.
            var commands = TeacherCommandCatalog.All
                .Where(command => command.Group == group && !TeacherCommandCatalog.AlwaysVisible.Contains(command.Id) &&
                                  !TeacherCommandCatalog.NeedsAdminKey(command.Id) && ModuleUiPolicy.CommandVisible(_settings, command.Id)).ToList();
            dlg.MultiSelect($"teacherGroup{i}", $"Visible in Teacher mode — {group}",
                commands.Select(command => command.Label).ToList(),
                commands.Where(command => _settings.TeacherVisibleCommands.Contains(command.Id, StringComparer.OrdinalIgnoreCase))
                    .Select(command => command.Label));
        }
        if (!dlg.Ask("Save")) return;

        _settings.Theme = dlg.S("theme");
        _settings.ButtonDisplay = ButtonDisplayValue(dlg.S("buttons"));
        var languageBefore = _settings.Language;
        _settings.Language = languages.CodeFor(dlg.S("language"));
        var modeBefore = _settings.HostMode;
        _settings.HostMode = dlg.S("hostMode") == HostModes.Admin ? HostModes.Admin : HostModes.Teacher;
        if (modeBefore != _settings.HostMode) WarnWhenAdminModeHasNoAdminKey();
        _settings.QuickActions = InterfaceSettingsPlan.QuickActionIds(dlg.S("quickActions"));
        _settings.QuickActionMessage = dlg.S("quickMessage");
        _settings.AlwaysShowExplanations = dlg.S("explanations") == "true";
        UiPreferences.Instance.AlwaysShowExplanations = _settings.AlwaysShowExplanations;
        _settings.TeacherVisibleCommands = InterfaceSettingsPlan.TeacherVisible(_settings.TeacherVisibleCommands,
            id => ModuleUiPolicy.CommandVisible(_settings, id), teacherGroups, i => dlg.S($"teacherGroup{i}"));
        _settings.Save();

        ThemeManager.Apply(Enum.TryParse<AppTheme>(_settings.Theme, out var theme) ? theme : AppTheme.Light);
        UiPreferences.Instance.Display = Enum.Parse<ButtonDisplayMode>(_settings.ButtonDisplay);
        BuildQuickActions();
        ApplyHostMode();
        // Every window takes its language when it opens, so a change needs a fresh start.
        if (!languageBefore.Equals(_settings.Language, StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(this, "The language changes when Classroom Manager starts again.\n\nRestart it now?", "Interface settings",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            RestartToApplySettings();
    }
}
