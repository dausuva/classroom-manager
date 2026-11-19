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
    async void Refresh_Click(object sender, RoutedEventArgs e) => await CheckAllAsync();

    void StartAutoRefresh()
    {
        _recurringWork.Start("refresh", TimeSpan.FromSeconds(30), async () =>
        {
            // An exam the command line started or ended while this window is open.
            if (_settings.AdoptExamChangeMadeElsewhere())
            {
                ExamModeToggle.IsChecked = _settings.ExamModeEnabled;
                UpdateExamModeVisualState();
                Log(_settings.ExamModeEnabled
                    ? $"Exam mode was turned on from the command line for {ExamModeComputers().Count} computer(s)."
                    : "Exam mode was turned off from the command line.");
            }
            if (!_settings.AutoRefreshEnabled) return;
            await CheckAllAsync();
            await _windowWork.RunAsync(FollowMovedComputersAsync);
        });
    }

    /// <summary>
    /// Looks for room computers that stopped answering at their saved address: one discovery broadcast,
    /// at most once a minute and only while some are missing. A computer that answers from another
    /// address is recognised by its network card address or client id and its entry follows it there,
    /// as Scan network would do. Nothing new is added to the list.
    /// </summary>
    async Task FollowMovedComputersAsync(CancellationToken cancellationToken)
    {
        await _discoveryWork.FollowAsync(new AddressFollowRuntime(this), cancellationToken);
    }

    /// <summary>Core adapter: discovery is observational; roster/trust/exam migration and authenticated recovery are not.</summary>
    sealed class AddressFollowRuntime(MainWindow owner) : IAddressFollowRuntime<StatusRefreshTarget, Discovery.Found>
    {
        readonly Discovery.NetworkSubnet? _network = owner._networkScope;
        readonly int _udpPort = owner._settings.UdpPort, _tcpPort = owner._settings.TcpPort;
        readonly string _configuredSubnet = owner._settings.DiscoverySubnet;
        public bool CanSearch => owner.ScanButton.IsEnabled && Equals(_network, owner._networkScope) &&
            _udpPort == owner._settings.UdpPort && _tcpPort == owner._settings.TcpPort && _configuredSubnet == owner._settings.DiscoverySubnet;

        bool StillMissing(StatusRefreshTarget target) => AddressFollowTargets.IsMissing(target, owner._clients, owner.ClientInSelectedNetwork);

        public IReadOnlyList<StatusRefreshTarget> CaptureMissing() => owner._clients
            .Where(c => owner.ClientInSelectedNetwork(c) && AddressFollower.IsMissing(c)).Select(StatusRefreshTarget.Capture).ToArray();

        public async Task<IReadOnlyList<Discovery.Found>> DiscoverAsync(CancellationToken cancellation) =>
            await Discovery.ScanAsync(owner._settings, _network, TimeSpan.FromSeconds(2), cancellation);

        public IReadOnlyList<StatusRefreshTarget> Apply(IReadOnlyList<StatusRefreshTarget> missing, IReadOnlyList<Discovery.Found> answers)
        {
            if (!CanSearch) return [];
            var moved = AddressFollowTargets.Plan(missing, owner._clients, owner.ClientInSelectedNetwork, answers.ToArray());
            var accepted = new List<StatusRefreshTarget>();
            foreach (var (target, answer) in moved)
            {
                // A preceding merge may have removed a duplicate row from this plan.
                if (!StillMissing(target)) continue;
                var computer = target.Client;
                owner.Log($"{computer.Name} now answers from {answer.Address} instead of {computer.Address}; following it.");
                var oldAddress = computer.Address;
                var merged = owner.AddDiscoveredClient(answer);
                owner._settings.MoveTrust(merged.Mac, merged.Address, merged.Port > 0 ? merged.Port : owner._settings.TcpPort);
                owner._exam.FollowAddress(merged.ClientId, oldAddress, merged.Address);
                accepted.Add(StatusRefreshTarget.Capture(merged));
            }
            return accepted;
        }

        public void Save() => owner.SaveComputers();
        public bool CanRefresh(StatusRefreshTarget target) => CanSearch && target.CanStart() && owner._clients.Contains(target.Client) && owner.ClientInSelectedNetwork(target.Client);
        public Task RefreshAsync(StatusRefreshTarget target) => owner.CheckOneAsync(target.Client);
        public void RefreshFailed(StatusRefreshTarget target, Exception error)
        {
            if (!target.Matches() || !owner._clients.Contains(target.Client)) return;
            target.Client.Status = "Check failed: " + error.Message;
            target.Client.Level = "";
        }
        public void Completed() => owner.UpdateSummary();
        public void Failed(Exception error) => owner.Log("Looking for computers with a changed address failed: " + error.Message);
    }

    /// <summary>Checks room-managed computers that have no action running, in parallel.
    /// Discovery-only entries are deliberately not polled until an administrator assigns a room.</summary>
    async Task CheckAllAsync()
    {
        await _statusRefresh.RunAsync(
            () => _clients.Where(c => ClientInSelectedNetwork(c) && StatusRefreshTarget.IsIdleManaged(c))
                .Select(StatusRefreshTarget.Capture).ToArray(),
            target => _clients.Contains(target.Client) && ClientInSelectedNetwork(target.Client) && target.CanStart(),
            (target, _) => CheckOneAsync(target.Client),
            (target, error) =>
            {
                if (!target.Matches() || !_clients.Contains(target.Client)) return;
                target.Client.Status = "Check failed: " + error.Message;
                target.Client.Level = "";
            },
            () => { SaveComputers(); ForgetOldKeysIfDone(); UpdateSummary(); });
    }

    /// <summary>The question before a dangerous action (T-190): asked once, then dangerous actions are allowed for ten minutes.</summary>
    Task<StepUpAnswer> AskStepUpAsync(StepUpRequest request) => Dispatcher.InvokeAsync(() =>
    {
        // A worker can queue this prompt before the window closes and reach the dispatcher afterwards.
        if (_windowCallbacks.IsDisposed) return StepUpAnswer.Deny;
        var answer = MessageBox.Show(this,
            $"This is a dangerous action: {Loc.T(request.Action)}.\n\nFirst computer: {request.Computer}.\n\n" +
            "Allow dangerous actions on all computers for the next 10 minutes? You will not be asked again until then.",
            "Dangerous action", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes ? StepUpAnswer.Allow : StepUpAnswer.Deny;
    }).Task;

    /// <summary>Core identity/policy recovery around a typed observational probe. ICMP remains only a reachability hint.</summary>
    async Task CheckOneAsync(ClientInfo c)
    {
        var endpoint = StatusRefreshTarget.Capture(c);
        bool IsCurrent() => endpoint.Matches() && _clients.Contains(c);
        try
        {
            using var s = await ClientSession.OpenAsync(c, _settings);
            if (!IsCurrent()) return;
            var connection = new ClientStatusConnection(s);
            var observed = await _statusProbe.PingAsync(connection);
            if (!IsCurrent()) return;
            var info = observed.Ping;
            var clientId = info.ClientId;
            // DHCP gave this saved address to another computer: do not turn the entry into that one. It looks for
            // its own computer instead (the follower moves it, with its approval, once that answers elsewhere).
            if (AddressFollower.AnswersAsAnother(c, clientId, info.Mac))
            {
                var held = ComputerIdentity.FormatMac(info.Mac);
                var status = StatusOutcomes.AddressTaken(c.Address, held);
                if (c.Status != status) Log($"{c.Name}: {c.Address} now answers as another computer (card {held}); looking for {c.Name}.");
                SetUnreachable(c, "warn", status);
                return;
            }
            c.ComputerName = info.MachineName ?? c.ComputerName;
            c.User = info.UserName;
            if (clientId.Length > 0) c.ClientId = clientId;
            var mac = ComputerIdentity.NormalizeMac(info.Mac);
            if (mac.Length > 0) c.Mac = mac;
            endpoint = StatusRefreshTarget.Capture(c);
            // It answered over the certificate approved for this address, so that approval is this card's.
            // Kept in the settings and written with the next save (an entry is only ever changed from empty to this card).
            _settings.ClaimTrust(c.Address, c.Port > 0 ? c.Port : _settings.TcpPort, c.Mac);
            // A key was removed while this computer was off: finish it now that it answers (T-189).
            if (KeyRevocations.For(_settings, c).Count > 0)
            {
                var (removed, _) = await KeyRevocations.ApplyAsync(_settings, c, s);
                if (removed.Count > 0)
                {
                    _settings.Save();
                    Log($"{c.Name}: removed the access key(s) {string.Join(", ", removed)}, revoked while this computer was off.");
                }
            }

            if (!IsCurrent()) return;
            var presentation = new StatusPresentationTarget(endpoint, _clients, Dispatcher.VerifyAccess);
            if (!presentation.ApplyMetadata(StatusProjection.Metadata(observed, DateTime.Now))) return;
            var helper = info.UserAgent;
            var languageSupported = c.SupportsCommand(Cmd.WindowsPreferencesGet);
            if (!presentation.ApplyLanguage(StatusProjection.InterfaceLanguage(languageSupported, helper, info.InterfaceLanguage))) return;
            if (languageSupported && helper)
            {
                var language = await _statusProbe.InterfaceLanguageAsync(connection, supported: true, hasUser: true);
                if (!IsCurrent()) return;
                if (!presentation.ApplyLanguage(StatusProjection.InterfaceLanguage(true, true, info.InterfaceLanguage, language))) return;
            }
            if (!presentation.ApplyWindows("")) return;
            if (c.SupportsCommand(Cmd.ProcWindows))
            {
                var windows = await _statusProbe.OpenWindowsAsync(connection, supported: true);
                if (!IsCurrent()) return;
                if (!presentation.ApplyWindows(StatusProjection.OpenWindows(windows))) return;
            }
            if (!presentation.ApplyOnline(observed, new(Protocol.Major, Protocol.Minor, Protocol.Version))) return;

            var exam = await _exam.ReconcileAsync(c, new ExamPeer(s));
            if (exam.ClearedPending) Log($"{c.Name}: Exam mode turned off now that the computer answers.");
            if (exam.DevicesNotApplied) c.Status += " - exam device controls not applied";

            c.KeyId = SharedKey.Fingerprint(s.KeyUsed);
            // Clients that enforce access roles say what this key may do; show a restricted role beside the key.
            c.Role = info.Role;
            c.DesktopRuntime = info.DesktopRuntime;
            c.KeyName = StatusOutcomes.KeyLabel(s.KeyName, s.UsesPreviousKey, c.Role);
            if (s.UsesPreviousKey)
            {
                // Missed a key rotation (was offline, or the rotation was interrupted): finish it now.
                if (_settings.AutoFinishKeyRotation)
                {
                    try
                    {
                        await _keys.PushAsync(c, s, _settings.Psk);
                        Log($"Key rotation finished on {c.Name} (it missed the earlier change).");
                    }
                    catch (Exception ex)
                    {
                        (c.Level, c.Status) = StatusOutcomes.OldKey(autoFinish: true, ex.Message);
                    }
                }
                else
                {
                    (c.Level, c.Status) = StatusOutcomes.OldKey(autoFinish: false, null);
                }
            }
        }
        catch (Exception ex) when (StatusOutcomes.Immediate(ex, _settings.HasHostKeys) is not null)
        {
            if (!IsCurrent()) return;
            var (level, status) = StatusOutcomes.Immediate(ex, _settings.HasHostKeys)!.Value;
            SetUnreachable(c, level, status);
        }
        catch (Exception ex)
        {
            if (!IsCurrent()) return;
            bool pcOn = await IcmpPingAsync(endpoint.Address);
            if (!IsCurrent()) return;
            var (level, status) = StatusOutcomes.AfterPing(ex, pcOn);
            SetUnreachable(c, level, status);
        }
    }

    static void SetUnreachable(ClientInfo c, string level, string status)
    {
        c.Level = level;
        c.Status = status;
        // A refused key ("bad") came from the right computer; everything else may be a changed address.
        c.Unreachable = level != "bad";
        c.Ping = "";
        c.Clock = "";
        c.Uptime = "";
        c.BlockedWebsiteCount = null;
        c.ClockOffsetSec = null;
        if (level != "bad") c.User = "";
    }

    static async Task<bool> IcmpPingAsync(string address)
    {
        try
        {
            using var p = new Ping();
            var reply = await p.SendPingAsync(address, 1200);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    void UpdateSummary()
    {
        var networkClients = _clients.Where(ClientInSelectedNetwork).ToList();
        SummaryText.Text = RosterRules.Summary(networkClients, KeyRoleSummary.IsTeacherOnly(_clients.Select(client => client.Role)), DateTime.Now);
        UpdateCollectionState();
    }

    Discovery.SubnetSelection RefreshNetworkScope()
    {
        var selection = Discovery.SelectSubnet(_settings.DiscoverySubnet);
        _networkScope = selection.Subnet;
        (NetworkScopeText.Text, NetworkScopeText.ToolTip) = RosterRules.NetworkScope(selection, _settings.DiscoverySubnet);
        return selection;
    }

    // ---- shared key rotation ----------------------------------------------------------------------------
}
