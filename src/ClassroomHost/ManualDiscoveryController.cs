// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

public interface IManualDiscoveryView
{
    bool CanScan { get; }
    Discovery.NetworkSubnet? CurrentNetwork { get; }
    void SetScanning(bool scanning);
    Discovery.SubnetSelection Prepare();
    Task RefreshAsync();
    void RefreshRooms();
    void Log(string message);
}

public sealed record ManualDiscoveryBatch(IReadOnlyList<Discovery.Found> Computers, IReadOnlyList<EnrolmentOffer> Offers);

/// <summary>Trusted core adapter; owns manual discovery/enrolment presentation without a concrete window reference.</summary>
public sealed class ManualDiscoveryController
{
    readonly IManualDiscoveryView _view;
    readonly HostSettings _settings;
    readonly HostRoster _roster;
    readonly ManualDiscoveryWorkflow _workflow;
    readonly Action _save;
    readonly Func<Discovery.NetworkSubnet?, CancellationToken, Task<ManualDiscoveryBatch>> _discover;
    readonly Func<EnrolmentOffer, CancellationToken, Task<string>> _presented;

    public ManualDiscoveryController(IManualDiscoveryView view, HostSettings settings, HostRoster roster,
        DiscoveryWorkCoordinator coordinator, Action save,
        Func<Discovery.NetworkSubnet?, CancellationToken, Task<ManualDiscoveryBatch>>? discover = null,
        Func<EnrolmentOffer, CancellationToken, Task<string>>? presented = null)
    {
        _view = view;
        _settings = settings;
        _roster = roster;
        _workflow = new(coordinator);
        _save = save;
        _discover = discover ?? DiscoverAsync;
        _presented = presented ?? ((offer, ct) => AutoEnrol.PresentedAsync(settings, offer, ct));
    }

    async Task<ManualDiscoveryBatch> DiscoverAsync(Discovery.NetworkSubnet? network, CancellationToken cancellation)
    {
        var offers = new List<EnrolmentOffer>();
        var found = await Discovery.ScanAsync(_settings, network, TimeSpan.FromSeconds(3), cancellation, offers);
        return new(found, offers);
    }

    public Task<bool> ScanAsync(CancellationToken cancellation = default) => _workflow.RunAsync(new Runtime(this), cancellation);

    sealed class Runtime(ManualDiscoveryController owner) : IManualDiscoveryRuntime<ManualDiscoveryBatch>
    {
        Discovery.NetworkSubnet? _network;
        string _configured = "";
        int _udp, _tcp;
        bool _autoEnrol;
        string[] _keys = [];
        public bool CanScan => owner._view.CanScan;
        public bool IsCurrent => Equals(_network, owner._view.CurrentNetwork) && _configured == owner._settings.DiscoverySubnet &&
            _udp == owner._settings.UdpPort && _tcp == owner._settings.TcpPort && _autoEnrol == owner._settings.AutoEnrolNewComputers &&
            _keys.SequenceEqual(owner._settings.CurrentHostKeys().Select(key => key.Psk));
        public void SetScanning(bool scanning) => owner._view.SetScanning(scanning);

        public void Prepare()
        {
            var selection = owner._view.Prepare();
            _network = selection.Subnet;
            _configured = owner._settings.DiscoverySubnet;
            _udp = owner._settings.UdpPort;
            _tcp = owner._settings.TcpPort;
            _autoEnrol = owner._settings.AutoEnrolNewComputers;
            _keys = owner._settings.CurrentHostKeys().Select(key => key.Psk).ToArray();
            if (selection.ConfiguredSubnetUnavailable)
                owner._view.Log($"Saved discovery subnet {_configured} is not connected; using the preferred LAN instead.");
            owner._view.Log(_network is null ? "Scanning network using the Windows default IPv4 route..."
                : $"Scanning {_network.DisplayName}...");
        }

        public Task<ManualDiscoveryBatch> DiscoverAsync(CancellationToken cancellation) => owner._discover(_network, cancellation);
        public void Apply(ManualDiscoveryBatch batch)
        {
            foreach (var found in batch.Computers) owner._roster.Merge(found);
            owner._view.Log($"Discovery found {batch.Computers.Count} computer(s).");
        }

        public async Task EnrolAsync(ManualDiscoveryBatch batch)
        {
            if (batch.Offers.Count == 0 || !IsCurrent) return;
            var plan = AutoEnrol.Plan(owner._settings, batch.Offers);
            if (!_autoEnrol)
            {
                var waiting = plan.Count(decision => decision.Action == AutoEnrolAction.Enrol);
                if (waiting > 0) owner._view.Log($"{waiting} new computer(s) announced themselves; automatic enrolment is off (Settings > Host settings).");
                return;
            }
            var done = await AutoEnrol.ApplyAsync(owner._settings, plan, async (offer, ct) =>
            {
                if (!IsCurrent) return "";
                var seen = await owner._presented(offer, ct);
                return IsCurrent ? seen : "";
            }, addComputers: false, onEnrolled: offer =>
                owner._roster.Add(offer.Address.ToString(), offer.Address.ToString(), offer.Port == _tcp ? 0 : offer.Port));
            foreach (var decision in done.Where(decision => decision.Action != AutoEnrolAction.AlreadyApproved)) owner._view.Log(decision.Text);
        }

        public void Save() => owner._save();
        public Task RefreshAsync() => owner._view.RefreshAsync();
        public void Completed() => owner._view.RefreshRooms();
    }
}
