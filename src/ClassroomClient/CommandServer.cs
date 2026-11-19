// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>TCP server: optionally wraps each connection in TLS, authenticates it, then answers request/response pairs.</summary>
sealed class CommandServer
{
    static readonly HashSet<string> KnownCommands = typeof(Cmd).GetFields()
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
    readonly ClientConfig _config;
    readonly Func<Request, bool, CallerIdentity, CancellationToken, Task<Response>> _handle;
    readonly KeyStore _keys;
    readonly X509Certificate2? _tlsCert;
    readonly PreAuthGate _gate;
    readonly SessionCounter _sessions = new();
    readonly KeyCommandLimiter _commandLimits;
    readonly SecurityEvents? _events;
    readonly EnrolmentState? _enrolment;
    SecurityEvents? Events => _events ?? SecurityEvents.Current;

    public CommandServer(ClientConfig config, CommandRouter router, KeyStore keys, EnrolmentState? enrolment = null)
        : this(config, keys, router.HandleAsync, enrolment: enrolment) { }

    /// <summary>The SHA-256 of the TLS certificate this server presents (what the Host approves); null without TLS.</summary>
    /// <summary>When the TLS certificate this computer presents stops being valid (UTC); null without TLS.</summary>
    public DateTime? CertificateNotAfterUtc => _tlsCert?.NotAfter.ToUniversalTime();

    public byte[]? CertificateSha256 => _tlsCert is null ? null : System.Security.Cryptography.SHA256.HashData(_tlsCert.RawData);

    internal CommandServer(ClientConfig config, KeyStore keys,
        Func<Request, bool, CallerIdentity, CancellationToken, Task<Response>> handle, PreAuthGate? gate = null, SecurityEvents? events = null,
        X509Certificate2? tlsCertificate = null, EnrolmentState? enrolment = null)
    {
        _events = events;
        _enrolment = enrolment;
        _gate = gate ?? new PreAuthGate(config.Limits);
        _commandLimits = new KeyCommandLimiter(config.TeacherCommandLimits);
        _config = config;
        _handle = handle;
        _keys = keys;
        // Loaded once at startup (not per-connection) so we don't re-read the pfx from disk for every client.
        // A test can give a certificate of its own, so the real certificate store (in ProgramData) is never touched by a test.
        _tlsCert = tlsCertificate ?? (config.UseTls ? TlsCertificateStore.LoadOrCreate() : null);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _ = Task.Run(() => ReportRefusalsAsync(ct), ct);
        while (!ct.IsCancellationRequested)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Any, _config.TcpPort);
                listener.Start();
                Log.Write($"Listening on TCP {_config.TcpPort}");
                while (!ct.IsCancellationRequested)
                {
                    var tcp = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => HandleAsync(tcp, ct), ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Write($"Listener error: {ex.Message}");
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
            }
            finally { listener?.Stop(); }
        }
    }

    /// <summary>Once a minute, how many connections were turned away before any TLS or key work, and why (counts only).</summary>
    async Task ReportRefusalsAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var counts = _gate.TakeCounts();
                if (counts.Count > 0)
                {
                    var text = string.Join(", ", counts.Select(pair => $"{pair.Value} {pair.Key}"));
                    Log.Write("Connections refused before authentication in the last minute: " + text + ".");
                    Events?.Write("connections-refused", ("lastMinute", text));
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task HandleAsync(TcpClient tcp, CancellationToken ct)
    {
        var remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        var source = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        // Before any TLS or key work: too many connections in setup, too fast, or a source that keeps failing is dropped silently.
        if (_gate.TryEnter(source) != Admission.Admitted) { tcp.Dispose(); return; }
        var settingUp = true;
        void EndSetup() { if (settingUp) { settingUp = false; _gate.Leave(source); } }
        // Same-machine check, once per connection: is the peer this computer's own loopback
        // address or one of its own NIC addresses? If so, CommandRouter refuses the commands
        // that could shut down/restart/lock/update this PC out from under whoever is running it.
        bool sameMachine = NetInfo.IsLocalAddress((tcp.Client.RemoteEndPoint as IPEndPoint)?.Address);
        using (tcp)
        {
            try
            {
                tcp.NoDelay = true;
                Stream stream = tcp.GetStream();

                using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                hsCts.CancelAfter(_gate.Limits.SetupDeadline);

                if (_tlsCert is not null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _tlsCert,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = TlsProfile.Protocols, // TLS 1.2 and 1.3 only (T-186)
                    }, hsCts.Token);
                    stream = ssl; // SecureChannel disposes whatever stream it's given, so this is cleaned up with the channel below
                }

                // Which key completed the handshake decides who the caller is and what they may do.
                var accepted = _keys.Current;
                // Inside TLS the proof may cover the certificate (T-187): bound if the Host sends the bound form, plain from an older Host
                // unless this computer is set to require the bound form.
                var (opened, keyIndex) = await SecureChannel.AcceptAsync(
                    stream, accepted.Select(key => key.Bytes).ToList(), hsCts.Token,
                    SecureChannel.BindingOf(stream), _config.RequireChannelBinding);
                using var channel = opened;
                EndSetup();
                _gate.Succeeded(source);
                _enrolment?.MarkEnrolled();   // a Host proved the key: the Client stops announcing itself
                var credential = accepted[keyIndex];
                var caller = new CallerIdentity(credential.Name, credential.Role);
                var keyFingerprint = SharedKey.Fingerprint(credential.Psk);

                // One key cannot hold every slot of this Client: past the cap the connection gets one authenticated refusal.
                var limits = _gate.Limits;
                var slot = keyFingerprint;
                var holdsSlot = _sessions.TryOpen(slot, limits.MaxSessionsPerKey);
                if (!holdsSlot)
                {
                    Log.Write($"{remote} [key {keyFingerprint}] throttled (too many sessions).");
                    Events?.Write("throttled", ("source", source), ("key", keyFingerprint), ("reason", "too many sessions"));
                    // Never wait for an unaccounted peer to send its first request. Even refusal writes
                    // are bounded, so a peer that stops reading cannot retain the socket indefinitely.
                    using var rejection = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    rejection.CancelAfter(TimeSpan.FromSeconds(1));
                    await channel.SendJsonAsync(new Response
                    {
                        Ok = false,
                        Error = "Too many connections from this key. Close some and try again.",
                        Data = new JsonObject { ["code"] = "throttled", [ErrorCatalog.PayloadKey] = ErrorCatalog.Throttled.Id }
                    }, rejection.Token);
                    // Let an existing Host finish its write and read the refusal before TCP closes.
                    // Discard bytes with fixed memory; never parse or execute a rejected request.
                    // The same one-second deadline also closes peers that remain idle or keep sending.
                    var discard = new byte[4096];
                    try { while (await stream.ReadAsync(discard, rejection.Token) > 0) { } }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    catch (IOException) { }
                    return;
                }
                try
                {
                    var requests = new RequestBucket(limits.RequestBurst, limits.RequestsPerSecond);
                    var loggedThrottle = false;
                    while (!ct.IsCancellationRequested)
                    {
                        var request = await channel.ReceiveJsonAsync<Request>(ct);
                        if (!requests.TryTake())
                        {
                            if (!loggedThrottle)
                            {
                                loggedThrottle = true;
                                Log.Write($"{remote} [key {keyFingerprint}] throttled (too many requests).");
                                Events?.Write("throttled", ("source", source), ("key", keyFingerprint), ("reason", "too many requests"));
                            }
                            await channel.SendJsonAsync(new Response
                            {
                                Ok = false,
                                Error = "Too many requests at once. Wait a moment and try again.",
                                Data = new JsonObject { ["code"] = "throttled", [ErrorCatalog.PayloadKey] = ErrorCatalog.Throttled.Id }
                            }, ct);
                            continue;
                        }
                        if (!_keys.IsCurrent(credential))
                        {
                            Events?.Write("key-revoked-use", ("source", source), ("key", keyFingerprint));
                            await channel.SendJsonAsync(new Response
                            {
                                Ok = false,
                                Error = "The access key for this connection was revoked or changed. Reconnect using a current key.",
                                Data = new JsonObject { ["code"] = "key_revoked", [ErrorCatalog.PayloadKey] = ErrorCatalog.KeyRevoked.Id }
                            }, ct);
                            break; // Send the authenticated refusal, then dispose this connection.
                        }
                        if (request.Cmd is not null && !_commandLimits.TryTake(keyFingerprint, credential.Role, request.Cmd))
                        {
                            Events?.Write("command-throttled", ("source", source), ("key", keyFingerprint), ("command", request.Cmd));
                            await channel.SendJsonAsync(new Response
                            {
                                Ok = false,
                                Error = $"This key has sent '{request.Cmd}' too often in the last minute. Wait a little and try again.",
                                Data = new JsonObject { ["code"] = "throttled", [ErrorCatalog.PayloadKey] = ErrorCatalog.Throttled.Id }
                            }, ct);
                            continue;
                        }
                        Response response;
                        var commandName = request.Cmd is not null && KnownCommands.Contains(request.Cmd) ? request.Cmd : "unknown";
                        try
                        {
                            response = await _handle(request, sameMachine, caller, ct);
                        }
                        catch (Exception ex)
                        {
                            Log.Write($"{remote} [key {keyFingerprint}] {commandName} failed ({ex.GetType().Name}).");
                            response = Response.Fail(ex.Message);
                        }
                        if (request.Cmd != Cmd.FsRead && request.Cmd != Cmd.FsWrite)
                            Log.Write($"{remote} [key {keyFingerprint}] {commandName} -> ok={response.Ok}.");
                        await channel.SendJsonAsync(response, ct);
                    }
                }
                finally { _sessions.Close(slot); }
            }
            catch (AuthException ex) { Log.Write($"{remote} rejected: {ex.Message}"); if (settingUp) { _gate.Failed(source); Events?.Write("handshake-failed", ("source", source), ("reason", ex.Message)); } }
            // A connection that is closed, or that says nothing until the deadline, is not a wrong key: a port check, a Health run or a Host
            // starting up does exactly that. It is logged as dropped but does not count towards the wait (it would lock out whoever diagnoses the
            // Client); what it can do to the Client is bounded by the caps and the token bucket, which count it.
            catch (EndOfStreamException) { if (settingUp) Events?.Write("connection-dropped", ("source", source), ("reason", "the peer closed the connection")); }
            catch (OperationCanceledException) { if (settingUp && !ct.IsCancellationRequested) Events?.Write("connection-dropped", ("source", source), ("reason", "no proof within the setup deadline")); }
            // The network took the connection away (reset, aborted, closed under us): the same as a peer that leaves.
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Log.Write($"{remote} connection ended ({ex.GetType().Name}).");
                if (settingUp) Events?.Write("connection-dropped", ("source", source), ("reason", ex.GetType().Name));
            }
            catch (Exception ex) { Log.Write($"{remote} connection error ({ex.GetType().Name})."); if (settingUp) { _gate.Failed(source); Events?.Write("handshake-failed", ("source", source), ("reason", ex.GetType().Name)); } }
            finally { EndSetup(); }
        }
    }
}

/// <summary>Answers UDP discovery broadcasts from the host.</summary>
sealed class DiscoveryResponder
{
    readonly ClientConfig _config;
    readonly KeyStore? _keys;
    readonly DiscoveryLimiter _limiter;
    // Checking a probe costs a few HMACs, so even the checking is bounded; only a probe that verifies uses the answer allowance below,
    // which means forged probes from the Host's address cannot use up the Host's own allowance.
    readonly DiscoveryLimiter _checking = new(new DiscoveryLimits { PerSourcePerSecond = 50, PerSourcePerMinute = 2000, GlobalPerSecond = 500 });
    readonly ProbeReplayCache _replays = new();
    readonly EnrolmentState? _enrolment;
    readonly Func<byte[]?>? _certificate;

    /// <param name="enrolment">When given, and while it is open, a valid probe is answered with the certificate fingerprint (enrolment mode).</param>
    /// <param name="certificate">The SHA-256 of this Client's TLS certificate, for the enrolment reply.</param>
    public DiscoveryResponder(ClientConfig config, KeyStore? keys = null, DiscoveryLimiter? limiter = null,
        EnrolmentState? enrolment = null, Func<byte[]?>? certificate = null)
    {
        _config = config;
        _keys = keys;
        _enrolment = enrolment;
        _certificate = certificate;
        _limiter = limiter ?? new DiscoveryLimiter(config.Discovery);
    }

    /// <summary>The reply to an authenticated probe, or null when the probe is not valid for any key this computer accepts.</summary>
    internal byte[]? AnswerProbe(byte[] packet, DateTimeOffset now)
    {
        if (_keys is null) return null;
        foreach (var key in _keys.Current)
        {
            var discoveryKey = DiscoveryV2.DeriveKey(key.Bytes);
            if (!DiscoveryV2.TryReadProbe(packet, discoveryKey, now, TimeSpan.FromSeconds(60), out var nonce)) continue;
            if (!_replays.TryRemember(nonce)) return null;
            // Newly installed and not yet connected to by any Host: say which certificate to approve (only a lab key holder can read it).
            if (_enrolment?.IsOpen(_config.EnrolmentWindowDays) == true && _certificate?.Invoke() is { Length: 32 } certificate)
                return DiscoveryV2.CreateEnrolmentReply(discoveryKey, nonce, _config.TcpPort, Protocol.Minor, certificate);
            return DiscoveryV2.CreateReply(discoveryKey, nonce, _config.TcpPort, Protocol.Minor, _config.ClientId, NetInfo.PrimaryMac());
        }
        return null;
    }

    /// <summary>Whether the address belongs to a network this computer is on (IPv4 only; loopback counts).</summary>
    static bool OnLocalSubnet(IPAddress? address)
    {
        if (address is null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    if (DiscoveryLimiter.SameSubnet(address, unicast.Address, unicast.PrefixLength)) return true;
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return false;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var lastReport = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, _config.UdpPort));
                Log.Write($"Discovery listening on UDP {_config.UdpPort}");

                while (!ct.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(ct);
                    var authenticated = DiscoveryV2.LooksLikeProbe(result.Buffer);
                    if (!authenticated && Encoding.UTF8.GetString(result.Buffer) != Protocol.DiscoverMagic) continue;
                    var source = result.RemoteEndPoint.Address.ToString();
                    // Rationed before any work is done: the question can come from anyone and its source can be forged.
                    var allowed = (!_config.Discovery.LocalSubnetOnly || OnLocalSubnet(result.RemoteEndPoint.Address)) &&
                                  (authenticated ? _checking.TryAnswer(source) : _config.AllowLegacyDiscovery && _limiter.TryAnswer(source));
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromMinutes(1))
                    {
                        lastReport = DateTime.UtcNow;
                        if (_limiter.TakeDropped() is > 0 and var dropped) Log.Write($"Discovery questions left unanswered in the last minute: {dropped}.");
                    }
                    if (!allowed) continue;

                    if (authenticated)
                    {
                        // An invalid probe gets silence: a scanner learns nothing.
                        var answer = AnswerProbe(result.Buffer, DateTimeOffset.UtcNow);
                        if (answer is not null && _limiter.TryAnswer(source)) await udp.SendAsync(answer, result.RemoteEndPoint, ct);
                        continue;
                    }

                    var reply = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryReply
                    {
                        MachineName = Environment.MachineName,
                        ClientId = _config.ClientId,
                        Mac = NetInfo.PrimaryMac(),
                        TcpPort = _config.TcpPort,
                    }, Protocol.Json);
                    await udp.SendAsync(reply, result.RemoteEndPoint, ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Write($"Discovery error: {ex.Message}");
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
