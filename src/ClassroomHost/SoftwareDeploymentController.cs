// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Windows;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public delegate Task SoftwareDeploymentRunner(List<ClientInfo> targets, string title, Func<ClientInfo, ClientSession, Task> action,
    int maxParallel = -1, string warnings = "");

/// <summary>Window-scoped deployment presentation. No Client self-update, inventory admission or session ownership.</summary>
public sealed class SoftwareDeploymentController(Window owner, HostSettings settings, Func<List<ClientInfo>> selected,
    Func<List<ClientInfo>> inView, Func<string> room, Func<List<ClientInfo>, string, string, bool> supports,
    SoftwareDeploymentRunner run, Action<string> log)
{
    readonly Window _owner = owner;
    readonly HostSettings _settings = settings;
    readonly Func<List<ClientInfo>> _selected = selected;
    readonly Func<List<ClientInfo>> _inView = inView;
    readonly Func<string> _room = room;
    readonly Func<List<ClientInfo>, string, string, bool> _supports = supports;
    readonly SoftwareDeploymentRunner _run = run;
    readonly Action<string> _log = log;
    string MyStaging => StagingNamespace.Root(_settings);

    /// <summary>The install dialog, with "Selected computers" meaning <paramref name="picked"/>.</summary>
    public async Task InstallAsync(List<ClientInfo> picked)
    {
        // Who to run on: the highlighted rows, everyone in the current room/list, or only computers that are online.
        var inView = _inView();
        var onlineInView = SoftwareInstallPlan.Online(inView);
        var targetChoices = SoftwareInstallPlan.TargetChoices(picked.Count, inView.Count, onlineInView.Count, _room());

        var dlg = new InstallSoftwareDialog(_owner, targetChoices, SoftwareInstallPlan.DefaultTargetIndex(picked.Count));
        if (dlg.ShowDialog() != true) return;

        var targets = SoftwareInstallPlan.Targets(dlg.TargetIndex, picked, inView, onlineInView);
        if (targets.Count == 0)
        {
            MessageBox.Show(_owner, SoftwareInstallPlan.NoComputers);
            return;
        }
        int batch = dlg.BatchSize;
        var level = dlg.Level;
        var file = dlg.LocalFile;
        var remotePath = dlg.RemotePath;
        var args = dlg.Arguments;
        var id = dlg.PackageId;

        var title = SoftwareInstallPlan.Title(dlg.Action, id, file, remotePath);

        var request = new SoftwareDeploymentRequest(dlg.Action, level, file, remotePath, args, id, dlg.RegistryView);
        await _run(targets, title, (client, session) =>
            new ClientSoftwareDeployment(client, session, MyStaging, _log).DeployAsync(request), maxParallel: batch);
    }

    public async Task StagedUploadsAsync()
    {
        var targets = _selected();
        if (targets.Count == 0) return;
        if (!_supports(targets, Cmd.StagingList, "Temporary upload management")) return;
        var window = new StagedUploadsWindow(targets, _settings, _owner);
        if (window.ShowDialog() != true || window.SelectedInstallRelativePath.Length == 0) return;

        var relative = window.SelectedInstallRelativePath;
        var isMsi = SoftwareInstallPlan.IsMsi(relative);
        var levelChoice = SoftwareInstallPlan.Elevated;
        var args = SoftwareInstallPlan.DefaultArguments(isMsi);
        var timeoutText = "1800";
        var deleteAfter = false;
        var operationWarning = "";
        string level;
        int timeoutSec;
        while (true)
        {
            var dlg = new FormDialog("Install uploaded software — Configure", _owner)
                .Note("Step 2 of 5 — Configure how the synchronized installer will run.")
                .Combo("level", "Run as", [SoftwareInstallPlan.Elevated, SoftwareInstallPlan.StandardUser],
                    levelChoice.StartsWith("Standard", StringComparison.Ordinal) ? 1 : 0)
                .Text("args", isMsi ? "MSI options and properties" : "EXE silent parameters", args)
                .Text("timeout", "Stop and report failure after this many seconds", timeoutText)
                .Check("delete", "Delete the staged installer from each computer after a successful install", deleteAfter);
            if (!dlg.Ask("Review")) return;

            levelChoice = dlg.S("level");
            deleteAfter = dlg.B("delete");
            var choices = SoftwareInstallPlan.Staged(relative, levelChoice, dlg.S("args"), dlg.S("timeout"), deleteAfter);
            args = choices.Arguments;
            timeoutText = choices.TimeoutText;
            level = choices.Level;
            timeoutSec = choices.TimeoutSeconds;
            operationWarning = choices.Warning;

            var targetText = OperationTargetSummary.Format(targets);
            var review = new BulkOperationReview(
                "Review uploaded software installation",
                targetText.Heading + "\n" + targetText.Detail,
                choices.ReviewItems,
                "Step 3 of 5 — Review before starting. During Run, the Operations panel shows per-computer results and elapsed-time installer heartbeats remain in Log.",
                operationWarning,
                choices.ReviewLabel);
            var decision = new BulkOperationReviewWindow(_owner, review).Ask();
            if (decision == BulkOperationReviewDecision.Back) continue;
            if (decision == BulkOperationReviewDecision.Cancel) return;
            break;
        }
        var remote = StagingNamespace.RemotePath(_settings, relative);
        var request = new StagedInstallerRequest(level, remote, args, timeoutSec, deleteAfter, StagingNamespace.Id(_settings), relative);
        await _run(targets, $"Install uploaded {Path.GetFileName(relative)}", (client, session) =>
            new ClientSoftwareDeployment(client, session, MyStaging, _log).InstallStagedAsync(request), warnings: operationWarning);
    }

}
