// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>The one call the exam workflow makes on a connected computer; <see cref="ClientSession"/> is the real one (through <see cref="ExamPeer"/>).</summary>
public interface IExamPeer
{
    Task<JsonObject> CallAsync(string cmd, JsonObject? args = null);
}

/// <summary>The non-owning adapter that lets the workflow run on a <see cref="ClientSession"/>.</summary>
public sealed class ExamPeer(ClientSession session) : IExamPeer
{
    public Task<JsonObject> CallAsync(string cmd, JsonObject? args = null) => session.CallAsync(cmd, args);
}

/// <summary>
/// Exam mode as a restriction the Host keeps true (MW-G): which computers it was turned on for, which still have to be told it is off, what is sent to a
/// computer and when a computer that was away gets it. It is a trusted core service, not a module or a plugin surface. The scope lists in the settings are
/// the only state; every change of them happens under one gate and is saved before anything is sent, so a Host that is closed or crashes half-way still
/// knows what each computer must be told. A computer that cannot be reached stays on the list and is told when it answers a status refresh.
/// </summary>
public sealed class ExamModeWorkflow
{
    readonly HostSettings _settings;
    readonly object _gate = new();

    public ExamModeWorkflow(HostSettings settings) => _settings = settings;

    public bool InExamMode(ClientInfo client) =>
        ExamModeScope.Contains(_settings.ExamModeComputers, client.ClientId, client.Address);

    /// <summary>The computers Exam mode was turned on for, whichever room or network is shown now.</summary>
    public List<ClientInfo> Computers(IEnumerable<ClientInfo> all) => all.Where(InExamMode).ToList();

    /// <summary>
    /// Records the change before it is sent. Turning on (not a re-apply) starts the list afresh with <paramref name="members"/>; turning off puts every member on
    /// the list of computers still to be told, and empties the list of computers it is on for.
    /// </summary>
    public void Begin(bool enable, bool reapply, IReadOnlyList<ClientInfo> members)
    {
        lock (_gate)
        {
            if (enable)
            {
                if (!reapply) _settings.ExamModeComputers.Clear();
                foreach (var client in members)
                {
                    ExamModeScope.Add(_settings.ExamModeComputers, client.ClientId, client.Address);
                    ExamModeScope.Remove(_settings.ExamModePendingClear, client.ClientId, client.Address);
                }
            }
            else
            {
                foreach (var client in members)
                {
                    ExamModeScope.Add(_settings.ExamModePendingClear, client.ClientId, client.Address);
                    client.ExamModeApplied = false;
                }
                _settings.ExamModeComputers.Clear();
            }
            _settings.ExamModeEnabled = enable;
            _settings.Save();
        }
    }

    /// <summary>The websites and apps of the overlay, as the Client takes them.</summary>
    public JsonObject OverlayArguments() => new()
    {
        ["domains"] = new JsonArray(_settings.ExamModeDomains.Select(value => (JsonNode)value).ToArray()),
        ["apps"] = new JsonArray(_settings.ExamModeApps.Select(value => (JsonNode)value).ToArray()),
    };

    /// <summary>
    /// Turns the overlay on or off on one connected computer. <paramref name="afterOverlay"/> sees the Client's answer (the blocked-website count it shows).
    /// A computer counts as done only when everything it needs was sent.
    /// </summary>
    public async Task ApplyAsync(ClientInfo client, IExamPeer peer, bool enable, JsonObject overlay, Action<ClientInfo, JsonObject> afterOverlay)
    {
        var result = await peer.CallAsync(enable ? Cmd.ExamModeSet : Cmd.ExamModeClear, enable ? overlay.DeepClone().AsObject() : null);
        afterOverlay(client, result);
        if (enable) await ApplyDevicesAsync(client, peer);
        else lock (_gate) ExamModeScope.Remove(_settings.ExamModePendingClear, client.ClientId, client.Address);
        client.ExamModeApplied = enable;
    }

    /// <summary>
    /// Sends the exam overlay's device controls. Clients older than protocol 1.8 only know the website and app overlay, so they report a failure here instead of
    /// looking fully restricted. Turning Exam mode off needs no call: an updated Client restores the devices as part of clearing the overlay.
    /// </summary>
    public async Task ApplyDevicesAsync(ClientInfo client, IExamPeer peer)
    {
        var wanted = ExamDeviceControls.Describe(
            _settings.ExamModeMuteAudio, _settings.ExamModeDisableCamera, _settings.ExamModeBlockUsbStorage);
        if (!client.SupportsCommand(Cmd.ExamDevicesSet))
        {
            if (wanted.Length == 0) return;
            throw new InvalidOperationException(
                $"Website and app restrictions applied, but the device controls ({wanted}) need Client protocol {Protocol.RequiredVersion(Cmd.ExamDevicesSet)}. Update the client.");
        }
        try
        {
            // Sent even when nothing is selected, so controls switched off in settings are lifted.
            await peer.CallAsync(Cmd.ExamDevicesSet, new JsonObject
            {
                [ExamDeviceControls.MuteAudio] = _settings.ExamModeMuteAudio,
                [ExamDeviceControls.DisableCamera] = _settings.ExamModeDisableCamera,
                [ExamDeviceControls.BlockUsbStorage] = _settings.ExamModeBlockUsbStorage,
            });
        }
        catch (RemoteException ex)
        {
            throw new InvalidOperationException("Website and app restrictions applied; device controls: " + ex.Message);
        }
    }

    /// <summary>Saves, then counts the members that still have to be told: for on, those it is not applied on; for off, those still listed to be cleared.</summary>
    public int Waiting(bool enable, IReadOnlyList<ClientInfo> members)
    {
        lock (_gate)
        {
            _settings.Save();
            return enable
                ? members.Count(client => !client.ExamModeApplied)
                : members.Count(client => ExamModeScope.Contains(_settings.ExamModePendingClear, client.ClientId, client.Address));
        }
    }

    /// <summary>What a status refresh found it had to do for one computer that answered.</summary>
    public sealed record Reconciled(bool ClearedPending, bool DevicesNotApplied);

    /// <summary>
    /// A computer answered a status refresh: if it was away when Exam mode was turned off, turn it off there now; if Exam mode is on for it and it does not have
    /// it yet, apply it. A device control that fails must not make a reachable computer look offline, so that is reported, not thrown.
    /// </summary>
    public async Task<Reconciled> ReconcileAsync(ClientInfo client, IExamPeer peer)
    {
        bool wanted, clearPending;
        lock (_gate)
        {
            wanted = _settings.ExamModeEnabled && InExamMode(client);
            clearPending = !wanted && ExamModeScope.Contains(_settings.ExamModePendingClear, client.ClientId, client.Address);
        }
        var cleared = false;
        if (clearPending && client.SupportsCommand(Cmd.ExamModeClear))
        {
            // It was unreachable when Exam mode was turned off.
            await peer.CallAsync(Cmd.ExamModeClear);
            lock (_gate)
            {
                ExamModeScope.Remove(_settings.ExamModePendingClear, client.ClientId, client.Address);
                _settings.Save();
            }
            cleared = true;
        }
        var devicesFailed = false;
        // Only the computers Exam mode was turned on for: this refresh covers every room.
        if (wanted && !client.ExamModeApplied && client.SupportsCommand(Cmd.ExamModeSet))
        {
            await peer.CallAsync(Cmd.ExamModeSet, OverlayArguments());
            try { await ApplyDevicesAsync(client, peer); }
            catch (InvalidOperationException) { devicesFailed = true; }
            client.ExamModeApplied = true;
        }
        return new Reconciled(cleared, devicesFailed);
    }

    /// <summary>A computer was found at a new address: Exam mode remembers a computer by address too, and the old address may soon belong to another computer.</summary>
    public void FollowAddress(string clientId, string oldAddress, string newAddress)
    {
        lock (_gate)
            foreach (var scope in new[] { _settings.ExamModeComputers, _settings.ExamModePendingClear })
            {
                if (!ExamModeScope.Contains(scope, clientId, oldAddress)) continue;
                ExamModeScope.Remove(scope, "", oldAddress);
                ExamModeScope.Add(scope, clientId, newAddress);
            }
    }
}
