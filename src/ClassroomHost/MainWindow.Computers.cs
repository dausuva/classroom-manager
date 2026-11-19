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
    /// The first-connection question of SSH: look at the certificate the computer presents (nothing is sent), show it with what is known,
    /// and ask whether to trust it. Nothing is typed; the person compares the fingerprint with Classroom Client Health on that computer.
    /// </summary>
    async void ApproveCertificate_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Selected();
        foreach (var client in chosen)
        {
            var computer = new SavedComputer
            {
                Address = client.Address, Name = client.ComputerName, Alias = client.Alias, ClientId = client.ClientId,
                Mac = ComputerIdentity.NormalizeMac(client.Mac), Port = client.Port, Room = client.Room, KeyId = client.KeyId,
            };
            AuditProbe probe;
            Mouse.OverrideCursor = Cursors.Wait;
            try { probe = await TrustCheckCli.ProbeAsync(computer, _settings, CancellationToken.None); }
            finally { Mouse.OverrideCursor = null; }
            var assessment = CertificateApproval.Assess(_settings, computer, probe.Presented, probe.ArpMac);
            if (!assessment.CanApprove)
            {
                Log(assessment.Headline);
                if (chosen.Count == 1)
                    MessageBox.Show(this, assessment.Headline + "\n\n" + string.Join("\n", assessment.Facts), "Approve certificate",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                continue;
            }
            var dialog = new FormDialog("Approve certificate", this).Note(assessment.Headline);
            if (assessment.Warning.Length > 0) dialog.Warning(assessment.Warning);
            dialog.Fingerprint("Certificate fingerprint (SHA-256)", CertificateApproval.Spaced(probe.Presented));
            foreach (var fact in assessment.Facts) dialog.Note(fact);
            dialog.Note("Compare the fingerprint with the one Classroom Client Health shows on that computer. Trust it only if they match, or you are sure this is your computer.");
            if (!dialog.Ask("Trust and connect"))
            {
                Log($"Certificate of {computer.Address} was not approved.");
                continue;
            }
            // Look again: what is approved must be what the person was shown, not what the computer presents a moment later.
            var again = await TrustCheckCli.ProbeAsync(computer, _settings, CancellationToken.None);
            if (!CertificateApproval.Approve(_settings, computer, probe.Presented, again.Presented, probe.ArpMac))
            {
                Log($"The certificate of {computer.Address} changed while it was shown; nothing was approved.");
                MessageBox.Show(this, "The computer presented a different certificate than the one shown. Nothing was approved.", "Approve certificate",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }
            SaveComputers();
            Log($"Certificate {probe.Presented[..16]}... approved for {computer.Address}.");
            await CheckOneAsync(client);
        }
    }

    async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new FormDialog("Add computer", this)
            .Note("Enter IP addresses or host names, separated by commas or spaces.")
            .Text("addr", "Address(es)");
        if (!dlg.Ask("Add")) return;

        var parts = dlg.S("addr").Split(new[] { ',', ' ', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts) AddClient(p, p);
        SaveComputers();
        await _windowWork.RunAsync(ScanAsync);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in Selected()) _clients.Remove(c);
        SaveComputers();
        RefreshRooms();
    }

    /// <summary>
    /// Removes the client from one computer (client.uninstall). The client answers before it goes; this Host then
    /// waits until the computer stops answering, since there is no one left to report how it went.
    /// </summary>
    async void UninstallClient_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected();
        if (targets.Count == 0) return;
        if (targets.Count != 1)
        {
            MessageBox.Show(this, ClientUninstallPlan.OnlyOne, "Uninstall client", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!EnsureFeatureSupported(targets, Cmd.ClientUninstall, "Uninstall client")) return;
        var client = targets[0];
        var dlg = new FormDialog("Uninstall client", this)
            .Target(targets)
            .Note($"Removes Classroom Client from {client.Name}: the service, the program, its firewall rules and, unless kept, its settings, " +
                  "logs, certificate and each user's files. What it set is put back first: exam controls, browser policies, the default " +
                  "browser and the sign-in picture. Afterwards this Host cannot reach the computer until it is set up again from the kit.")
            .Text("name", $"Type the computer's name, {client.Name}, to confirm")
            .Check("keep", "Keep the client's settings for a later reinstall (the same key and computer identity)", false)
            .Check("labTrust", "Also remove the lab trust: the lab code-signing certificate this client is signed with " +
                               "(other Dausuva lab software signed with it is then no longer trusted on this computer)", false);
        if (!dlg.Ask("Uninstall")) return;
        if (!ClientUninstallPlan.NameMatches(dlg.S("name"), client.Name))
        {
            MessageBox.Show(this, ClientUninstallPlan.NameMismatch(client.Name), "Uninstall client", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        bool keep = dlg.B("keep"), labTrust = dlg.B("labTrust");
        bool removed = false;
        await RunOnAsync([client], $"Uninstall client from {client.Name}", async (c, s) =>
        {
            var started = ClientUninstallPlan.ReadAnswer(await s.CallAsync(Cmd.ClientUninstall, ClientUninstallPlan.Arguments(keep, labTrust)));
            Log($"  {c.Name}: removal started; the computer writes what it does to {started.LogPath}");
            if (started.LabTrustFingerprint is { } fingerprint)
                Log($"  {c.Name}: the lab trust {fingerprint} is removed last");
            else if (started.LabTrustNote is { } note)
                Log($"  {c.Name}: lab trust not removed: {note}");
            c.Status = "Removing the client...";
            removed = await ClientGoneAsync(c);
            if (!removed)
                throw new InvalidOperationException(@"The computer still answers after 3 minutes. Look at C:\Windows\Temp\ClassroomClient-uninstall.log on it.");
            c.Status = "Client removed";
        });
        if (removed && MessageBox.Show(this, $"The client was removed from {client.Name}. Take {client.Name} off this list too?",
                "Uninstall client", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _clients.Remove(client);
            SaveComputers();
            RefreshRooms();
        }
    }

    /// <summary>True once the client's port has refused or ignored three tries in a row, within 3 minutes.</summary>
    /// <summary>True when the computer has stopped answering on the client's port (three silent answers in a row within three minutes).</summary>
    Task<bool> ClientGoneAsync(ClientInfo client)
    {
        var port = client.Port > 0 ? client.Port : _settings.TcpPort;
        return ClientUninstallPlan.WaitUntilGoneAsync(() => ClientUninstallPlan.AnswersAsync(client.Address, port), ClientUninstallPlan.Interval, ClientUninstallPlan.Timeout);
    }

    void RemoveOffline_Click(object sender, RoutedEventArgs e)
    {
        var gone = _view.Cast<ClientInfo>().Where(c => c.Level == "" && c.Status.StartsWith("Offline")).ToList();
        if (gone.Count == 0) { MessageBox.Show(this, "No offline computers in the current network and room view."); return; }
        if (MessageBox.Show(this, $"Remove {gone.Count} offline computer(s) from the current network and room view?\n\n" +
                string.Join("\n", gone.Take(10).Select(c => c.Name)) + (gone.Count > 10 ? "\n..." : ""),
                "Remove offline", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        foreach (var c in gone) _clients.Remove(c);
        SaveComputers();
        RefreshRooms();
    }

    /// <summary>Saves the computer list (names, addresses, MACs, rooms) so it can be loaded on another host. The key is NOT included.</summary>
    void ExportList_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = "classroom-computers.json", Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        SaveComputers();
        System.IO.File.WriteAllText(dlg.FileName,
            System.Text.Json.JsonSerializer.Serialize(_settings.Computers, new System.Text.Json.JsonSerializerOptions(Protocol.Json) { WriteIndented = true }));
        Log($"Exported {_settings.Computers.Count} computer(s) to {dlg.FileName}");
    }

    async void ImportList_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<SavedComputer>>(
                System.IO.File.ReadAllText(dlg.FileName), Protocol.Json) ?? new();
            int added = MergeComputers(list);
            SaveComputers();
            RefreshRooms();
            Log($"Imported {list.Count} computer(s), {added} new.");
            await CheckAllAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not import: " + ex.Message); }
    }

    /// <summary>Adds computers from a saved list; one already in the list keeps what it has and only gains what was missing. Returns how many are new.</summary>
    int MergeComputers(IEnumerable<SavedComputer> list)
    {
        int added = 0;
        foreach (var sc in list.Where(x => x.Address.Length > 0))
        {
            var existing = _clients.FirstOrDefault(c =>
                ComputerIdentity.StableMatch(c.ClientId, c.Mac, sc.ClientId, sc.Mac));
            existing ??= _clients.FirstOrDefault(c =>
                ComputerIdentity.SafeAddressMatch(c, sc.Address, sc.ClientId, sc.Mac));
            if (existing is null)
            {
                _clients.Add(new ClientInfo
                {
                    Address = sc.Address, ComputerName = sc.Name, Alias = sc.Alias, ClientId = sc.ClientId,
                    Mac = ComputerIdentity.NormalizeMac(sc.Mac), Port = sc.Port, Room = sc.Room, KeyId = sc.KeyId,
                    KeyName = _settings.KeyNameFromFingerprint(sc.KeyId),
                    Status = sc.Room.Length > 0 ? "Not checked" : "Imported - assign to a room to monitor",
                });
                added++;
            }
            else
            {
                if (existing.Room.Length == 0) existing.Room = sc.Room;
                if (existing.ClientId.Length == 0) existing.ClientId = sc.ClientId;
                if (existing.Mac.Length == 0) existing.Mac = ComputerIdentity.NormalizeMac(sc.Mac);
                if (existing.KeyId.Length == 0) existing.KeyId = sc.KeyId;
            }
        }
        return added;
    }
}
