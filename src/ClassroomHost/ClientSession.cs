// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>One authenticated connection to one client.</summary>
public sealed class ClientSession : IDisposable
{
    readonly TcpClient _tcp;
    readonly SecureChannel _channel;
    readonly ClientInfo _client;
    readonly bool _allowExclusiveOperation;
    public event Action<FileTransferProgress>? TransferProgress;

    void ReportTransfer(FileTransferProgress progress) => TransferProgress?.Invoke(progress);

    /// <summary>The shared key this connection authenticated with.</summary>
    public string KeyUsed { get; }

    /// <summary>Friendly lab name of the key this connection authenticated with.</summary>
    public string KeyName { get; }

    /// <summary>True for the default lab key and its temporary rotation fallback keys.</summary>
    public bool IsDefaultKey { get; }

    /// <summary>True when the key proof of this connection covered the TLS certificate (protocol 1.17 and later, T-187).</summary>
    public bool IsBound => _channel.IsBound;

    /// <summary>True when the client refused the current key but accepted one from before a rotation.</summary>
    public bool UsesPreviousKey { get; }

    ClientSession(TcpClient tcp, SecureChannel channel, ClientInfo client, bool allowExclusiveOperation,
        string keyUsed, string keyName, bool isDefaultKey, bool usesPreviousKey)
    {
        _tcp = tcp;
        _channel = channel;
        _client = client;
        _allowExclusiveOperation = allowExclusiveOperation;
        KeyUsed = keyUsed;
        KeyName = keyName;
        IsDefaultKey = isDefaultKey;
        UsesPreviousKey = usesPreviousKey;
    }

    /// <summary>
    /// Prefers the key fingerprint remembered for this computer, then tries every current named lab
    /// key, followed by temporary keys from an unfinished default-key rotation.
    /// </summary>
    public static async Task<ClientSession> OpenAsync(ClientInfo client, HostSettings settings, CancellationToken ct = default,
        bool allowExclusiveOperation = false)
    {
        try { return await OpenCoreAsync(client, settings, ct, allowExclusiveOperation); }
        catch (Exception ex) when (Tagged(ex) is { } tagged) { throw tagged; }
    }

    /// <summary>The same exception with its error id in the message (T-134), or null when it already has one or cannot carry one.</summary>
    static Exception? Tagged(Exception ex)
    {
        var tagged = ErrorClassifier.WithId(ex);
        return ReferenceEquals(tagged, ex) ? null : tagged;
    }

    static async Task<ClientSession> OpenCoreAsync(ClientInfo client, HostSettings settings, CancellationToken ct, bool allowExclusiveOperation)
    {
        ThrowIfExclusive(client, allowExclusiveOperation);
        if (KeyRefusalPause.EndsAt(client) is { } pausedUntil) throw KeyRefusalPause.NotTried(pausedUntil);
        var defaultName = settings.NormalizedDefaultKeyName();
        var candidates = settings.CurrentHostKeys()
            .Select(x => new KeyCandidate(x.Psk, x.Name, x.IsDefault, false))
            .Concat(settings.PreviousPsks.Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => new KeyCandidate(x, defaultName, true, true)))
            // A computer given its own key (T-188) holds one derived from the lab key and its client id; tried after the lab keys themselves
            // and, once it has worked, first (the remembered fingerprint puts it there).
            .Concat(client.ClientId.Length == 0 ? [] : settings.CurrentHostKeys()
                .Select(x => new KeyCandidate(DeviceKeys.Derive(x.Psk, client.ClientId), x.Name, x.IsDefault, false)))
            .GroupBy(x => x.Psk, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(x => SharedKey.Fingerprint(x.Psk).Equals(client.KeyId, StringComparison.OrdinalIgnoreCase) ? 0 :
                          x.UsesPreviousKey ? 2 : 1)
            .ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException("No host key is configured. Open Lab keys and add the key for this classroom.");

        Exception? lastAuthenticationFailure = null;
        AuthException? explicitAuthenticationFailure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var opened = await OpenWithKeyCoreAsync(client, settings, candidate.Psk, ct, candidate.UsesPreviousKey,
                    candidate.Name, candidate.IsDefaultKey, allowExclusiveOperation);
                KeyRefusalPause.Clear(client);
                return opened;
            }
            // A refused key normally surfaces as AuthException; over TLS an abrupt close can arrive as an IOException.
            catch (Exception ex) when (ex is AuthException || ex is IOException)
            {
                lastAuthenticationFailure = ex;
                explicitAuthenticationFailure ??= ex as AuthException;
            }
        }

        // Every key was refused: stop connecting for a while, because each try renews the computer's block.
        if (explicitAuthenticationFailure is not null) KeyRefusalPause.Start(client);

        // A key was refused and a later try was dropped without an answer: the computer may have started to ignore this PC after too many refused keys,
        // so the key that would have worked was never really tried. Say that instead of only "wrong key".
        if (explicitAuthenticationFailure is not null && lastAuthenticationFailure is IOException && candidates.Count > 1)
            throw new AuthException(explicitAuthenticationFailure.Message + " A later try was dropped without an answer: the computer may be ignoring this PC for a few minutes after refused keys; wait about five minutes.");
        throw explicitAuthenticationFailure ?? lastAuthenticationFailure ??
              new AuthException("None of the configured host keys was accepted.");
    }

    /// <summary>Connects with exactly this key (used to verify a key change).</summary>
    public static async Task<ClientSession> OpenWithKeyAsync(ClientInfo client, HostSettings settings, string key,
        CancellationToken ct = default, bool usesPreviousKey = false, string keyName = "Default lab",
        bool isDefaultKey = true, bool allowExclusiveOperation = false)
    {
        try { return await OpenWithKeyCoreAsync(client, settings, key, ct, usesPreviousKey, keyName, isDefaultKey, allowExclusiveOperation); }
        catch (Exception ex) when (Tagged(ex) is { } tagged) { throw tagged; }
    }

    static async Task<ClientSession> OpenWithKeyCoreAsync(ClientInfo client, HostSettings settings, string key,
        CancellationToken ct, bool usesPreviousKey, string keyName, bool isDefaultKey, bool allowExclusiveOperation)
    {
        ThrowIfExclusive(client, allowExclusiveOperation);
        int port = client.Port > 0 ? client.Port : settings.TcpPort;
        var certificateValidator = settings.UseTls ? TlsEndpointTrust.Validator(settings.TlsPins(client.Address, port, client.Mac)) : null;
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                await tcp.ConnectAsync(client.Address, port, cts.Token);
                Stream stream = tcp.GetStream();

                if (settings.UseTls)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
                        // Independent exact endpoint enrollment, before any PSK proof or command.
                        userCertificateValidationCallback: certificateValidator);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = client.Address,
                        EnabledSslProtocols = TlsProfile.Protocols,
                    }, cts.Token);
                    stream = ssl;
                }

                // A Client that understands the bound form gets a proof that covers its TLS certificate; an older one gets the plain proof.
                var binding = Protocol.IsAtLeast(client.Version, Protocol.ChannelBindingVersion) ? SecureChannel.BindingOf(stream) : null;
                var channel = await SecureChannel.ConnectAsync(stream, Encoding.UTF8.GetBytes(key), cts.Token, binding);
                return new ClientSession(tcp, channel, client, allowExclusiveOperation, key, keyName, isDefaultKey, usesPreviousKey);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Connection timed out.");
            }
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    sealed record KeyCandidate(string Psk, string Name, bool IsDefaultKey, bool UsesPreviousKey);

    static void ThrowIfExclusive(ClientInfo client, bool allowed)
    {
        if (!allowed && client.ExclusiveOperation.Length > 0)
            throw new InvalidOperationException($"{client.ExclusiveOperation} Other actions are paused to avoid interrupting it.");
    }

    /// <summary>Sends a command; throws if the client reports an error.</summary>
    public Task<JsonObject> CallAsync(string cmd, JsonObject? args = null, CancellationToken ct = default) =>
        CallAtAsync(Levels.System, cmd, args, ct);

    /// <summary>Sends a command to the elevated service (Levels.System) or the signed-in user's helper (Levels.User).</summary>
    public async Task<JsonObject> CallAtAsync(string level, string cmd, JsonObject? args = null, CancellationToken ct = default)
    {
        try { return await CallCoreAsync(level, cmd, args, ct); }
        catch (Exception ex) when (Tagged(ex) is { } tagged) { throw tagged; }
    }

    async Task<JsonObject> CallCoreAsync(string level, string cmd, JsonObject? args, CancellationToken ct)
    {
        ThrowIfExclusive(_client, _allowExclusiveOperation);
        await StepUp.EnsureAsync(cmd, _client.Name.Length > 0 ? _client.Name : _client.Address, ct);
        await _channel.SendJsonAsync(new Request { Cmd = cmd, Level = level, Args = args ?? new JsonObject() }, ct);
        var response = await _channel.ReceiveJsonAsync<Response>(ct);
        if (!response.Ok)
        {
            var error = response.Error ?? "Unknown error";
            var code = response.Data["code"]?.GetValue<string>();
            if (code == "unsupported_command" || error.StartsWith("Unknown command", StringComparison.OrdinalIgnoreCase))
                throw new UnsupportedFeatureException(cmd, RequiredProtocol(cmd, response.Data["requiredProtocol"]?.GetValue<string>()), response.Data);
            throw new RemoteException(error, response.Data);
        }
        return response.Data;
    }

    /// <summary>
    /// The protocol a command needs, for the "update the client" message. A client too old to know
    /// the command can only answer with the baseline version, so this Host's own table wins whenever
    /// it knows the command as a later addition.
    /// </summary>
    public static string RequiredProtocol(string command, string? reportedByClient)
    {
        var known = Protocol.RequiredVersion(command);
        return known != "1.0" || string.IsNullOrEmpty(reportedByClient) ? known : reportedByClient;
    }

    /// <summary>Uploads a local file to a path on the client, chunk by chunk.</summary>
    public async Task UploadAsync(string localPath, string remotePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(localPath);
        long total = fs.Length, offset = 0;
        var meter = new FileTransferMeter(FileTransferDirection.Upload, total, ReportTransfer);
        meter.Report(0);
        var buf = new byte[Protocol.ChunkSize];
        do
        {
            int n = await fs.ReadAsync(buf, ct);
            await CallAsync(Cmd.FsWrite, new JsonObject
            {
                ["path"] = remotePath,
                ["offset"] = offset,
                ["data"] = Convert.ToBase64String(buf, 0, n),
                ["total"] = total,
                ["complete"] = offset + n >= total,
            }, ct);
            offset += n;
            meter.Report(offset, complete: offset >= total);
            progress?.Report(total == 0 ? 1 : (double)offset / total);
        } while (offset < total);
    }

    /// <summary>Reuses an exact-path staging file when its byte length matches the local file.</summary>
    public async Task<bool> UploadIfDifferentSizeAsync(string localPath, string remotePath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var localLength = new FileInfo(localPath).Length;
        var directory = Path.GetDirectoryName(remotePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            try
            {
                var listing = await CallAsync(Cmd.FsList, new JsonObject { ["path"] = directory }, ct);
                if (RemoteFileListing.Matches(listing, Path.GetFileName(remotePath), localLength))
                {
                    progress?.Report(1);
                    return false;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* A metadata check must not prevent the normal upload fallback. */ }
        }

        await UploadAsync(localPath, remotePath, progress, ct);
        return true;
    }

    /// <summary>Downloads a file from the client to a local path.</summary>
    public async Task DownloadAsync(string remotePath, string localPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await using var fs = File.Create(localPath);
        long offset = 0;
        var started = TimeProvider.System.GetTimestamp();
        FileTransferMeter? meter = null;
        while (true)
        {
            var data = await CallAsync(Cmd.FsRead, new JsonObject { ["path"] = remotePath, ["offset"] = offset }, ct);
            var bytes = Convert.FromBase64String(data["data"]!.GetValue<string>());
            await fs.WriteAsync(bytes, ct);
            offset += bytes.Length;
            long total = data["total"]!.GetValue<long>();
            if (meter is null)
            {
                meter = new FileTransferMeter(FileTransferDirection.Download, total, ReportTransfer, startedTimestamp: started);
                meter.Report(0);
            }
            meter.Report(offset, complete: data["eof"]!.GetValue<bool>() || bytes.Length == 0);
            progress?.Report(total == 0 ? 1 : (double)offset / total);
            if (data["eof"]!.GetValue<bool>() || bytes.Length == 0) break;
        }
    }

    public void Dispose()
    {
        TransferProgress = null;
        _channel.Dispose();
        _tcp.Dispose();
    }
}

public static class RemoteFileListing
{
    public static bool Matches(JsonObject listing, string fileName, long length) =>
        listing["entries"]?.AsArray().Any(node =>
            node is JsonObject entry &&
            entry["isDir"]?.GetValue<bool>() == false &&
            entry["name"]?.GetValue<string>().Equals(fileName, StringComparison.OrdinalIgnoreCase) == true &&
            entry["size"]?.GetValue<long>() == length) == true;
}

public class RemoteException : Exception
{
    /// <summary>The client's raw error payload. Named Payload (not Data) so it doesn't
    /// hide the unrelated <see cref="Exception.Data"/> dictionary.</summary>
    public JsonObject Payload { get; }
    public RemoteException(string message, JsonObject data) : base(message) => Payload = data;
}

public sealed class UnsupportedFeatureException : RemoteException
{
    public string Command { get; }
    public string RequiredProtocol { get; }

    public UnsupportedFeatureException(string command, string requiredProtocol, JsonObject data)
        : base(ErrorIds.WithId($"This feature is not supported by the remote Classroom Client. Update it to protocol {requiredProtocol} or later and try again.",
            ErrorCatalog.FeatureNeedsNewerClient), data)
    {
        Command = command;
        RequiredProtocol = requiredProtocol;
    }
}
