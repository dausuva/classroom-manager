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
    async void Wake_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        await DoWakeAsync(targets);
    }

    // ---- software -----------------------------------------------------------------------

    async void Install_Click(object sender, RoutedEventArgs e) => await _softwareDeployment.InstallAsync(SelectedRaw());

    async void StagedUploads_Click(object sender, RoutedEventArgs e) => await _softwareDeployment.StagedUploadsAsync();

    async void UpdateClient_Click(object sender, RoutedEventArgs e)
    {
        if (ClientUpdatePlan.InProgress(_clients))
        {
            MessageBox.Show(this, "A client update is already in progress. Other actions are paused until every update attempt finishes.",
                "Client update in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var picked = SelectedRaw();
        var inView = _view.Cast<ClientInfo>().ToList();
        var onlineInView = ClientUpdatePlan.Online(inView);
        var targetChoices = ClientUpdatePlan.TargetChoices(picked.Count, inView.Count, onlineInView.Count, _room);

        var dlg = new FormDialog("Update client", this)
            .Combo("targets", "Update on", targetChoices, ClientUpdatePlan.DefaultChoice(picked.Count))
            .Text("batch", "How many computers at once (blank = all at once, 1 = one by one)", "8")
            .File("file", "New ClassroomClient.exe (from Publish-Client.ps1)", "ClassroomClient.exe|ClassroomClient.exe|All files (*.exe)|*.exe")
            .Check("force", "Apply even if it isn't newer than the running client")
            .Note("Uploads the new client, then each PC's service replaces itself and restarts (a few seconds of downtime; " +
                  "other jobs on that PC will fail while it does). The signed-in student's per-user helper picks up the " +
                  "new build at their next sign-in, same as after a fresh install.");
        if (!dlg.Ask("Update")) return;

        var choice = Array.IndexOf(targetChoices, dlg.S("targets"));
        var targets = ClientUpdatePlan.Targets(choice, picked, inView, onlineInView);
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "No computers to update. Select some in the list, or choose another option under \"Update on\".");
            return;
        }
        int batch = ClientUpdatePlan.Batch(dlg.S("batch"));

        var file = dlg.S("file");
        if (!System.IO.File.Exists(file))
        {
            MessageBox.Show(this, "Choose the new ClassroomClient.exe to push out (build it first with Publish-Client.ps1).");
            return;
        }
        bool force = dlg.B("force");

        var runtime = ClientUpdatePlan.ScreenRuntime(targets, ClientBuildRuntime.Describe(file));
        if (!ConfirmFit(runtime)) return;
        targets = runtime.Fitting;

        var architecture = ClientUpdatePlan.ScreenArchitecture(targets, PeArchitecture.Read(file));
        if (!ConfirmFit(architecture)) return;
        targets = architecture.Fitting;

        var sha256 = ClientUpdatePlan.Sha256Of(file);

        foreach (var client in targets) client.ExclusiveOperation = "A Classroom Client update is in progress.";
        MessageBox.Show(this,
            "Client update is starting. Other actions for these computers are paused until all update attempts finish, because another command could interrupt the upload or service restart.",
            "Client update protection", MessageBoxButton.OK, MessageBoxImage.Information);
        try
        {
            await RunOnAsync(targets, ClientUpdatePlan.Title(file), async (c, s) =>
            {
                var remote = $@"{MyStaging}\update\ClassroomClient.exe";
                var progress = new Progress<double>(p => c.Status = $"Uploading update {p:P0}");
                await s.UploadAsync(file, remote, progress);
                c.Status = "Applying update...";
                await s.CallAsync(Cmd.SelfUpdate, ClientUpdatePlan.Arguments(remote, sha256, force));
                c.Status = "Update sent (service restarting)";
            }, maxParallel: batch, allowExclusiveOperation: true);
        }
        finally
        {
            foreach (var client in targets) client.ExclusiveOperation = "";
        }
    }

    /// <summary>True when the update may go on with the computers that fit: all of them, or the rest after the user agreed to leave the others out.</summary>
    bool ConfirmFit(ClientUpdatePlan.Screening screening)
    {
        switch (screening.Fit)
        {
            case ClientUpdatePlan.Fit.All:
                return true;
            case ClientUpdatePlan.Fit.None:
                MessageBox.Show(this, screening.Explanation, "Update client", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            default:
                return MessageBox.Show(this, screening.Explanation, "Update client", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                    MessageBoxResult.Cancel) == MessageBoxResult.OK;
        }
    }
}
