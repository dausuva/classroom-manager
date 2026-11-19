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
    /// rsync-like copy from each student computer's folder into this computer's folder (one subfolder per
    /// computer): only new or changed files travel, and nothing is deleted on either side.
    /// </summary>
    async Task SyncFilesAsync(CustomQuickAction action, List<ClientInfo> targets)
    {
        var localRoot = action.LocalFolder.Trim();
        var pattern = FolderSyncPlan.Pattern(action);
        await RunOnAsync(targets, action.Name, async (c, s) =>
        {
            var found = await s.CallAtAsync(Levels.User, Cmd.FsFind, new JsonObject
            {
                ["folder"] = action.Target,
                ["pattern"] = pattern,
                ["recursive"] = action.IncludeSubfolders,
            });
            var (remoteFolder, remote) = FolderSyncPlan.ReadFound(found);
            var computerDir = System.IO.Path.Combine(localRoot, FileTransferWorkflow.Sanitize(c.Name));
            LocalFile? Local(string relative)
            {
                var info = new System.IO.FileInfo(System.IO.Path.Combine(computerDir, relative));
                return info.Exists ? new LocalFile(info.Length, info.LastWriteTime) : null;
            }
            var copy = FolderSync.ToCopy(remote, Local, action.Overwrite).Select(file => (
                System.IO.Path.Combine(remoteFolder, file.RelativePath), System.IO.Path.Combine(computerDir, file.RelativePath), file.Modified));
            var (n, failed) = await FileTransferWorkflow.DownloadFilesAsync(new ClientSessionPeer(s), copy, text => c.Status = text);
            c.Status = FolderSyncPlan.Status(n, remote.Count, failed.Count);
            Log($"  {c.Name}: {n} new or changed of {remote.Count} file(s) -> {computerDir}");
            foreach (var problem in failed) Log($"  {c.Name}: not copied: {problem}");
        });
    }

    async void SendFiles_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;

        var pick = new OpenFileDialog { Multiselect = true, Title = "Choose files to send to the students" };
        if (pick.ShowDialog(this) != true) return;

        var files = pick.FileNames;
        var chosen = files.Select(path => (Name: Path.GetFileName(path), Size: new FileInfo(path).Length)).ToList();
        // One field: a usual folder picked from the list puts its value in the field, and any other path can be typed.
        var folder = "%DESKTOP%";
        var subfolder = "";
        var overwrite = true;
        var destination = folder;
        var operationWarning = "";
        FileSendPlan plan;
        while (true)
        {
            var dlg = new FormDialog("Send files", this)
                .Target(targets)
                .Note($"{files.Length} file(s) selected. Files are placed in the student's own folder, owned by the student.")
                .Choice("dest", "Put the files in (choose one, or type a full path)", CollectFolders.OnStudentComputer, folder)
                .Text("subfolder", "Optional subfolder inside it (created if missing)", subfolder)
                .Check("overwrite", "Overwrite files with the same name", overwrite);
            if (!dlg.Ask("Review")) return;

            folder = dlg.S("dest");
            subfolder = PathText.Clean(dlg.S("subfolder"));
            overwrite = dlg.B("overwrite");
            var resolved = FileDestination.Resolve(folder, subfolder, out var problem);
            if (resolved is null) { MessageBox.Show(this, problem); continue; }
            destination = resolved;

            plan = FileSendPlan.Build(chosen, destination, overwrite);
            var targetText = OperationTargetSummary.Format(targets);
            var review = new BulkOperationReview(
                "Review file transfer",
                targetText.Heading + "\n" + targetText.Detail,
                plan.ReviewItems,
                "Step 3 of 5 — Review before starting. Run progress and per-computer results appear in Operations; completed deliveries are recorded in Transfer history for safe undo.",
                plan.Warning,
                "Send files");
            var decision = new BulkOperationReviewWindow(this, review).Ask();
            if (decision == BulkOperationReviewDecision.Back) continue;
            if (decision == BulkOperationReviewDecision.Cancel) return;
            operationWarning = review.Warning;
            break;
        }

        var history = new ConcurrentBag<FileTransferRecord>();

        await RunOnAsync(targets, plan.Title,
            (c, s) => FileTransferWorkflow.SendAsync(new ClientSessionPeer(s), c, files, MyStaging, destination, overwrite, history.Add, text => c.Status = text),
            warnings: operationWarning);
        if (!history.IsEmpty)
        {
            _settings.FileTransferHistory.AddRange(FileSendPlan.HistoryOrder(history));
            _settings.Save();
        }
    }

    void TransferHistory_Click(object sender, RoutedEventArgs e) =>
        new FileTransferHistoryWindow(_settings, _clients, this).ShowDialog();

    void RecentFiles_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0 || !EnsureFeatureSupported(targets, Cmd.FsRecent, "Recent files")) return;
        new RecentFilesWindow(targets, _settings, this).Show();
    }

    async void Collect_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;

        var dlg = new FormDialog("Collect files from students", this)
            .Target(targets)
            .Note("Copies matching files from each student's folder into a folder on this computer, " +
                  "one subfolder per student. The originals stay where they are.")
            .Choice("folder", "Folder on the student PC (choose one, or type a full path)", CollectFolders.OnStudentComputer, "%DOCUMENTS%")
            .Text("pattern", "File name pattern (e.g. *.docx, homework*.*, *)", "*.*")
            .Check("recursive", "Include subfolders", false)
            .Folder("local", "Save into (on this computer)", _settings.RecentCollectFolders.FirstOrDefault() ?? "",
                CollectFolders.OnThisComputer(_settings.RecentCollectFolders))
            .Check("stamp", "Put results in a dated subfolder", true)
            .Check("overwrite", "Overwrite files that are already in the destination", true);
        if (!dlg.Ask("Collect")) return;

        var plan = CollectPlan.Build(dlg.S("local"), dlg.B("stamp"), DateTime.Now, dlg.S("folder"), dlg.S("pattern"), dlg.B("recursive"), dlg.B("overwrite"));
        if (plan.Problem is { } problem) { MessageBox.Show(this, problem); return; }
        _settings.RememberCollectFolder(plan.ChosenFolder);
        _settings.Save();
        var (localRoot, folder, pattern, recursive, overwrite) = (plan.LocalRoot, plan.StudentFolder, plan.Pattern, plan.Recursive, plan.Overwrite);

        await RunOnAsync(targets, "Collect files", async (c, s) =>
        {
            var result = await FileTransferWorkflow.CollectAsync(new ClientSessionPeer(s), c, localRoot, folder, pattern, recursive, overwrite, text => c.Status = text);
            Log($"  {c.Name}: {result.Copied} file(s) -> {result.StudentFolder}");
            if (result.Kept > 0) Log($"  {c.Name}: {result.Kept} file(s) already in the destination were kept");
            foreach (var problem in result.Failed) Log($"  {c.Name}: not copied: {problem}");
        });
    }

    void Files_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        if (targets.Count == 1) new FileManagerWindow(targets[0], _settings) { Owner = this }.Show();
        else new BulkFileManagerWindow(targets, _settings, this).Show();
    }

    // ---- classroom ---------------------------------------------------------------------------
}
