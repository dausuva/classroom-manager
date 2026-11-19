// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClassroomManager.Host;

/// <summary>
/// <c>ClassroomCli trust-check</c>: lists the computers whose approved certificate or address no longer fits, from
/// the saved settings alone or, with --live, from what the network shows now; makes the safe fixes with --fix; and
/// approves one computer's certificate with --approve when the person gives the start of the fingerprint read on it.
/// </summary>
public static class TrustCheckCli
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    static extern int SendARP(uint destination, uint source, byte[] mac, ref int length);

    public static async Task<int> RunAsync(HostSettings settings, string path, CliOptions options, TextWriter output, bool hostOpen)
    {
        var writes = options.Fix || options.Approve || ((options.FromFiles.Length > 0 || options.Restore.Length > 0) && options.Yes);
        if (writes && hostOpen && Path.GetFullPath(path).Equals(Path.GetFullPath(HostSettings.PathOnDisk), StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("Close Classroom Host first: an open Host keeps its own settings and would write them back over this change.");

        if (options.ListBackups)
        {
            var all = TrustBackups.List(path);
            if (all.Count == 0) output.WriteLine("No backups made by the trust tools were found beside " + path + ".");
            for (var i = 0; i < all.Count; i++) output.WriteLine($"{i + 1,3}  {all[i].Describe()}");
            return HostCli.ExitOk;
        }

        if (options.Restore.Length > 0)
        {
            var backup = TrustBackups.Find(path, options.Restore);
            var backupFile = TrustBackups.SettingsFileOf(backup, path);
            output.WriteLine("Backup: " + backup.Describe());
            var changes = TrustBackups.Compare(path, backupFile, options.WithComputers);
            if (changes.Count == 0)
            {
                output.WriteLine("The backup and the current settings agree on " + (options.WithComputers ? "the approvals and the computers." : "the approvals.") + " Nothing to restore.");
                return HostCli.ExitOk;
            }
            foreach (var change in changes) output.WriteLine("  " + change);
            if (!options.Yes) { output.WriteLine("Nothing was changed. Add --yes (Host closed) to restore this."); return HostCli.ExitOk; }
            var safety = TrustBackups.Restore(path, backupFile, options.WithComputers, DateTime.Now);
            output.WriteLine($"Restored. The settings before were copied to {safety}.");
            return HostCli.ExitOk;
        }

        if (options.FromFiles.Length > 0)
        {
            var plan = EnrolmentImport.Plan(settings, options.FromFiles, DateTime.UtcNow);
            output.WriteLine(EnrolmentImport.Text(plan));
            var pending = plan.Count(line => line.Action == EnrolmentAction.Enrol);
            if (pending == 0) return plan.Any(line => line.Action is EnrolmentAction.Rejected or EnrolmentAction.DifferentApproval) ? HostCli.ExitFailures : HostCli.ExitOk;
            if (!options.Yes) { output.WriteLine("Nothing was changed. Add --yes (Host closed) to make these approvals."); return HostCli.ExitOk; }
            var backup = path + ".trust-enrol-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
            File.Copy(path, backup);
            var made = EnrolmentImport.Apply(settings, plan);
            settings.Save(path);
            output.WriteLine($"{made} approval(s) made. The settings before were copied to {backup}.");
            return HostCli.ExitOk;
        }

        if (options.Reservations)
        {
            output.WriteLine(TrustAudit.Reservations(settings));
            return HostCli.ExitOk;
        }

        var live = options.Live || writes;
        var scope = options.Computers.Count > 0 ? CliTargets.Resolve(settings.Computers, options) : settings.Computers.ToList();
        var sightings = new List<AuditSighting>();
        var probes = new List<AuditProbe>();
        var offers = new List<EnrolmentOffer>();
        if (live)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var subnet = Discovery.SelectSubnet(settings.DiscoverySubnet).Subnet;
            if (subnet is not null)
                foreach (var found in await Discovery.ScanAsync(settings, subnet, TimeSpan.FromSeconds(3), cancellation.Token, offers))
                    sightings.Add(new AuditSighting(found.Address.ToString(), ComputerIdentity.NormalizeMac(found.Info.Mac), found.Info.ClientId ?? "", found.Info.MachineName ?? ""));
            using var gate = new SemaphoreSlim(options.Parallel);
            probes.AddRange(await Task.WhenAll(scope.Select(async computer =>
            {
                await gate.WaitAsync(cancellation.Token);
                try { return await ProbeAsync(computer, settings, cancellation.Token); }
                finally { gate.Release(); }
            })));
        }

        if (options.Approve)
        {
            var computer = scope[0];
            var approvedPin = TrustAudit.Approve(settings, computer, probes[0].Presented, options.Check);
            settings.Save(path);
            output.WriteLine($"Approved {approvedPin} for {computer.Name} ({computer.Address}), with its network card {ComputerIdentity.FormatMac(computer.Mac)} recorded where it is known.");
            return HostCli.ExitOk;
        }

        // The audit looks at the computers in scope; the rest are neither probed nor reported.
        var narrowed = new HostSettings { TcpPort = settings.TcpPort, TrustedTlsEndpoints = settings.TrustedTlsEndpoints, Computers = scope };
        var report = TrustAudit.Analyze(narrowed, sightings, probes, live);

        // Newly installed computers that announced their certificate under this lab's key (enrolment mode, T-185).
        var enrolments = AutoEnrol.Plan(settings, offers);
        var waitingToEnrol = enrolments.Where(decision => decision.Action != AutoEnrolAction.AlreadyApproved).ToList();
        if (options.Fix && waitingToEnrol.Any(decision => decision.Action == AutoEnrolAction.Enrol))
        {
            var enrolBackup = path + ".trust-enrol-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
            File.Copy(path, enrolBackup);
            var enrolled = await AutoEnrol.ApplyAsync(settings, enrolments, (offer, ct) => AutoEnrol.PresentedAsync(settings, offer, ct), addComputers: true);
            settings.Save(path);
            output.WriteLine($"{enrolled.Count(decision => decision.Action == AutoEnrolAction.Enrol)} new computer(s) enrolled. The settings before were copied to {enrolBackup}.");
            foreach (var decision in enrolled.Where(decision => decision.Action != AutoEnrolAction.AlreadyApproved)) output.WriteLine("  - " + decision.Text);
            waitingToEnrol = [];
        }

        if (options.Fix)
        {
            if (report.Fixes.Count == 0) output.WriteLine("Nothing to fix.");
            else
            {
                var backup = path + ".trust-fix-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                File.Copy(path, backup);
                var applied = TrustAudit.Apply(settings, report.Fixes);
                settings.Save(path);
                output.WriteLine($"{applied} fix(es) made. The settings before were copied to {backup}.");
                foreach (var fix in report.Fixes) output.WriteLine("  - " + fix.Describe);
            }
        }

        if (options.Json)
            output.WriteLine(JsonSerializer.Serialize(new
            {
                healthy = report.Healthy,
                computers = report.Lines.Select(line => new { name = line.Computer.Name, address = line.Computer.Address, mac = line.Computer.Mac, state = line.State.ToString(), detail = line.Detail, command = line.Command }),
                notes = report.Notes,
                fixes = report.Fixes.Select(fix => fix.Describe),
            }, new JsonSerializerOptions { WriteIndented = true }));
        else
        {
            output.WriteLine(TrustAudit.Text(report, live));
            if (waitingToEnrol.Count > 0)
            {
                output.WriteLine();
                output.WriteLine("New computers announcing themselves (enrolment mode):");
                foreach (var decision in waitingToEnrol) output.WriteLine("  - " + decision.Text);
                if (waitingToEnrol.Any(decision => decision.Action == AutoEnrolAction.Enrol))
                    output.WriteLine("  Enrol them with: ClassroomCli trust-check --live --fix --yes   (Host closed). An open Host does it by itself on a scan.");
            }
        }
        return report.Lines.Any(line => line.State is AuditState.AddressClash or AuditState.NeedsApproval or AuditState.CertOfAnother) ? HostCli.ExitFailures : HostCli.ExitOk;
    }

    /// <summary>One look-only connection: the certificate the computer presents (nothing is sent over it) and the card the network reports for its address.</summary>
    public static async Task<AuditProbe> ProbeAsync(SavedComputer computer, HostSettings settings, CancellationToken ct)
    {
        var port = computer.Port > 0 ? computer.Port : settings.TcpPort;
        var presented = "";
        try
        {
            using var tcp = new TcpClient();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(computer.Address, port, limit.Token);
            using var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
            {
                if (certificate is not null) presented = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
                return false;   // look only: the handshake is refused, so no key or command is ever sent
            });
            try { await ssl.AuthenticateAsClientAsync(computer.Address); }
            catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException) { }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException) { }
        return new AuditProbe(computer.Address, port, presented, ArpMac(computer.Address));
    }

    /// <summary>The card the network says holds this IPv4 address on this LAN; empty when it is not on it.</summary>
    public static string ArpMac(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return "";
        try
        {
            var mac = new byte[6];
            var length = mac.Length;
            return SendARP(BitConverter.ToUInt32(ip.GetAddressBytes(), 0), 0, mac, ref length) == 0 && length == 6 ? Convert.ToHexString(mac) : "";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return ""; }
    }
}
