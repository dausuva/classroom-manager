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
    void AccessKeys_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        if (!EnsureFeatureSupported(targets, Cmd.AccessKeysList, "Access keys")) return;
        new AccessKeysWindow(targets, _settings, this,
            (operationTargets, title, action, maxParallel, warnings, targetFinished) =>
                RunOnAsync(operationTargets, title, action, maxParallel: maxParallel, warnings: warnings,
                    targetFinished: targetFinished)).Show();
    }

    void SecureLab_Click(object sender, RoutedEventArgs e) =>
        new LabSecurityWindow(this, _settings, Selected().Count > 0 ? Selected() : _clients.ToList()).Show();

    void LabKeys_Click(object sender, RoutedEventArgs e)
    {
        new LabKeysWindow(this, _settings).ShowDialog();
        foreach (var client in _clients) client.KeyName = _settings.KeyNameFromFingerprint(client.KeyId);
    }

    async void RotateKey_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_settings.Psk))
        {
            MessageBox.Show(this, "Enter the current shared key in Settings first.");
            return;
        }

        var picked = SelectedRaw();
        var all = _clients.Where(ClientInSelectedNetwork).ToList();
        var inView = _view.Cast<ClientInfo>().ToList();
        var choices = KeyRotationPlan.Choices(all, _room, inView, picked);
        var labels = choices.Select(x => x.Label).ToArray();

        var dlg = new FormDialog("Rotate shared key", this)
            .Note("Gives the student computers a new shared key. Each computer is checked with a fresh connection " +
                  "on the new key before it counts as done; if that check fails it keeps its current key.")
            .Combo("targets", "Change the key on", labels, KeyRotationPlan.DefaultChoice(picked.Count, _room, labels.Length))
            .Text("key", "New key (a random one is filled in; you can type your own, at least 12 characters)", SharedKey.Generate())
            .Text("batch", "How many computers at once (blank = all at once)", "8")
            .Check("auto", "Computers that are offline now get the new key automatically the next time this app sees them",
                   _settings.AutoFinishKeyRotation);
        if (_settings.PreviousPsks.Count > 0)
            dlg.Note($"{_settings.PreviousPsks.Count} older key(s) from an unfinished rotation are still accepted until every computer has switched.");
        if (!dlg.Ask("Rotate")) return;

        var newKey = dlg.S("key");
        if (KeyRotationPlan.Refusal(newKey, _settings.Psk) is { } refusal) { MessageBox.Show(this, refusal); return; }
        var targets = KeyRotationPlan.Pick(choices, dlg.S("targets")).Targets;
        if (targets.Count == 0) { MessageBox.Show(this, "No computers to change. Select some, or choose another option."); return; }
        var incompatibleTargets = _keys.IncompatibleTargets(targets);
        if (incompatibleTargets.Count > 0)
        {
            MessageBox.Show(this, KeyRotationPlan.IncompatibleText(incompatibleTargets), "Different lab key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int batch = KeyRotationPlan.Batch(dlg.S("batch"));

        var plan = KeyChangePlan.Build(targets, _settings.KeyGraceDays, _settings.NormalizedDefaultKeyName());
        var confirm = new FormDialog("Rotate shared key", this)
            .Target(targets);
        foreach (var line in plan.Lines()) confirm.Note(line);
        confirm.Text("typed", $"To go on, type the name of the lab key ({plan.KeyName})");
        if (!confirm.Ask("Change key")) return;
        if (!plan.Confirms(confirm.S("typed")))
        {
            MessageBox.Show(this, "That is not the name of the lab key. Nothing was changed.", "Rotate shared key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StepUp.Grant();   // the typed lab key name above is a stronger confirmation than the step-up question
        // Saved on this PC first: if the app is closed half-way it still knows both keys and can finish later.
        var oldKey = _settings.Psk;
        _keys.Begin(newKey, dlg.B("auto"), Log);
        Log($"Shared key changed on this PC (new key {SharedKey.Fingerprint(newKey)}, previous {SharedKey.Fingerprint(oldKey)}).");

        int changed = 0;
        await RunOnAsync(targets, "Rotate key", async (c, s) =>
        {
            if (!s.IsDefaultKey)
                throw new InvalidOperationException($"this computer uses the '{s.KeyName}' lab key, not the default lab key");
            c.Status = "Changing key...";
            bool already = await _keys.PushAsync(c, s, newKey);
            c.Status = already ? "Done: already on the new key" : "Done: key changed and verified";
            changed++;
        }, maxParallel: batch);
        SaveComputers();
        ForgetOldKeysIfDone();
        UpdateSummary();

        int pending = _keys.NotOnNewKey(_clients, newKey);
        new FormDialog("New shared key", this)
            .Note(KeyRotationPlan.Summary(changed, targets.Count, pending, _settings.AutoFinishKeyRotation))
            .Text("key", "New key - copy it now for your other teacher PC (Settings) and your USB kit (deploy.ps1 -Key)", newKey)
            .Note("Keep it somewhere safe, such as a password manager: whoever has it can control every student PC.")
            .Ask("Close");
    }

    void ForgetOldKeysIfDone()
    {
        if (_keys.ForgetOldKeysIfDone(_clients))
            Log($"Every computer using '{_settings.NormalizedDefaultKeyName()}' has confirmed the new shared key; the old key is no longer used.");
    }

    // ---- time sync --------------------------------------------------------------------------------------
}
