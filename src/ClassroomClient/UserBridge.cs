// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Client;

/// <summary>Lets the SYSTEM service hand a request to the signed-in user's helper.</summary>
interface IUserForwarder
{
    /// <summary>Name of the user whose helper is connected, or null if nobody is signed in.</summary>
    string? CurrentUser { get; }
    /// <summary>Windows session of that user, or null if nobody is signed in.</summary>
    int? CurrentSession { get; }
    Task<Response> ForwardAsync(Request request, CancellationToken ct);
}

/// <summary>Message exchanged over the local named pipe (helper &lt;-&gt; service).</summary>
sealed class PipeMessage
{
    public int Id { get; set; }
    public Request? Req { get; set; }
    public Response? Res { get; set; }
    // hello (helper -> service, Id == 0)
    public string? User { get; set; }
    public int Session { get; set; }
}

static class PipeFraming
{
    public static async Task WriteAsync(Stream s, PipeMessage msg, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(msg, Protocol.Json);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await s.WriteAsync(frame, ct);
        await s.FlushAsync(ct);
    }

    public static async Task<PipeMessage> ReadAsync(Stream s, CancellationToken ct, int maxFrameBytes = Protocol.MaxFrameBytes)
    {
        var header = new byte[4];
        await ReadExactAsync(s, header, ct);
        int len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len <= 0 || len > Math.Min(maxFrameBytes, Protocol.MaxFrameBytes)) throw new InvalidDataException("Bad pipe frame.");
        var body = new byte[len];
        await ReadExactAsync(s, body, ct);
        return JsonSerializer.Deserialize<PipeMessage>(body, Protocol.Json) ?? throw new InvalidDataException("Empty pipe frame.");
    }

    static async Task ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }
}

/// <summary>
/// Service side of the bridge. Each signed-in user runs a helper that connects here;
/// user-level requests (wallpaper, mouse, lock, standard-user installs/commands) are forwarded to it.
/// </summary>
sealed class UserBridge : IUserForwarder
{
    public const string PipeName = "ClassroomManagerUserAgent";

    sealed class Helper
    {
        public required NamedPipeServerStream Pipe { get; init; }
        public string User { get; init; } = "";
        public string Sid { get; init; } = "";
        public int Session { get; init; }
        public int NextId;
        public readonly SemaphoreSlim WriteLock = new(1, 1);
        public readonly ConcurrentDictionary<int, TaskCompletionSource<Response>> Pending = new();
    }

    readonly List<Helper> _helpers = new();
    readonly object _gate = new();
    Request? _activeLock; // re-applied whenever a helper (re)connects, so the lock survives helper restarts
    readonly LockExpiry _lockExpiry = new();   // the service ends every lock itself after LockPolicy.DefaultMinutes unless the Host asked for another length

    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();

    public string? CurrentUser => Pick()?.User;
    public int? CurrentSession => Pick()?.Session;

    /// <summary>Session ids that currently have a helper connected - used by HelperSupervisor to spot sessions missing one.</summary>
    public IReadOnlySet<int> ConnectedSessions
    {
        get { lock (_gate) return _helpers.Select(h => h.Session).ToHashSet(); }
    }

    /// <summary>
    /// Which of the connected sessions a request goes to. A lock or its release goes to <b>every</b> session that has a helper: with fast user switching a second
    /// signed-in account is one click away, and a lock that only covers the account at the screen can be walked around (found on a real computer, 2026-11-04) or
    /// left behind in an account that was switched away from. Everything else (a message, a wallpaper, ...) goes to the user at the screen only.
    /// </summary>
    internal static IReadOnlyList<int> SessionsFor(string cmd, IReadOnlyList<int> connected, int console)
    {
        if (connected.Count == 0) return [];
        if (cmd is Cmd.LockSet or Cmd.LockClear) return connected;
        return [connected.LastOrDefault(session => session == console, connected[^1])];
    }

    Helper? Pick()
    {
        lock (_gate)
        {
            if (_helpers.Count == 0) return null;
            int console = unchecked((int)WTSGetActiveConsoleSessionId());
            return _helpers.LastOrDefault(h => h.Session == console) ?? _helpers[^1];
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                var security = new PipeSecurity();
                // Network logons must never use this same-machine bridge.
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Deny));
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));

                server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 65536, 65536, security);

                await server.WaitForConnectionAsync(ct);
                var connected = server;
                server = null;
                // Always enter the handler so its finally disposes the accepted pipe on cancellation.
                _ = ServeHelperAsync(connected, ct);
            }
            catch (OperationCanceledException) { server?.Dispose(); return; }
            catch (Exception ex)
            {
                server?.Dispose();
                Log.Write($"Pipe server error: {ex.Message}");
                try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    internal async Task ServeHelperAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        Helper? helper = null;
        try
        {
            using var registration = CancellationTokenSource.CreateLinkedTokenSource(ct);
            registration.CancelAfter(TimeSpan.FromSeconds(5));
            var hello = await PipeFraming.ReadAsync(pipe, registration.Token, maxFrameBytes: 4096);
            var peer = PipePeerIdentity.ReadHelper(pipe);
            PipePeerIdentity.ValidateHello(hello, peer);
            PipePeerIdentity.VerifySessionOwner(peer);
            lock (_gate)
            {
                // A second connection cannot take over a live helper for this Windows identity.
                if (_helpers.Any(existing => existing.Session == peer.Session))
                    throw new System.Security.SecurityException("Duplicate helper registration.");
                helper = new Helper { Pipe = pipe, User = peer.Name, Sid = peer.Sid, Session = peer.Session };
                _helpers.Add(helper);
            }
            Log.Write($"User helper connected (session {helper.Session})");

            var lockRequest = _activeLock;
            if (lockRequest is not null) _ = SendAsync(helper, lockRequest, ct);   // to the helper that just connected, not to whoever is at the screen

            while (true)
            {
                var msg = await PipeFraming.ReadAsync(pipe, ct);
                if (msg.Res is not null && helper.Pending.TryRemove(msg.Id, out var tcs))
                    tcs.TrySetResult(msg.Res);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (helper is null) Log.Write("User helper registration rejected.");
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (helper is not null)
            {
                lock (_gate) _helpers.Remove(helper);
                foreach (var kv in helper.Pending) kv.Value.TrySetResult(Response.Fail("The user helper disconnected."));
                Log.Write($"User helper disconnected (session {helper.Session})");
            }
            pipe.Dispose();
        }
    }

    public async Task<Response> ForwardAsync(Request request, CancellationToken ct)
    {
        // Remember the lock state even if nobody is signed in: it is applied when a helper connects.
        if (request.Cmd == Cmd.LockSet)
        {
            _activeLock = request;
            var minutes = LockPolicy.Minutes(request.Args);
            _lockExpiry.Start(TimeSpan.FromMinutes(minutes), async () =>
            {
                Log.Write($"The lock ended by itself after {minutes} minute(s).");
                await ForwardAsync(new Request { Cmd = Cmd.LockClear, Level = Levels.User }, CancellationToken.None);
            });
        }
        else if (request.Cmd == Cmd.LockClear)
        {
            _activeLock = null;
            _lockExpiry.Cancel();
        }

        Helper[] targets;
        lock (_gate)
        {
            var sessions = SessionsFor(request.Cmd, _helpers.Select(h => h.Session).ToList(), unchecked((int)WTSGetActiveConsoleSessionId()));
            targets = _helpers.Where(h => sessions.Contains(h.Session)).ToArray();
        }
        if (targets.Length == 0)
        {
            if (request.Cmd is Cmd.LockSet or Cmd.LockClear)
                return Response.Success(new JsonObject { ["queued"] = true });
            return Response.Fail("No user is signed in (the user-level helper is not running on this computer).");
        }

        // The answer is the one of the user at the screen when several sessions get the request; the others are sent it too and their answers are not awaited for the result.
        var answers = await Task.WhenAll(targets.Select(helper => SendAsync(helper, request, ct)));
        var console = unchecked((int)WTSGetActiveConsoleSessionId());
        var index = Array.FindIndex(targets, helper => helper.Session == console);
        return answers[index >= 0 ? index : 0];
    }

    async Task<Response> SendAsync(Helper helper, Request request, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref helper.NextId);
        var tcs = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        helper.Pending[id] = tcs;
        try
        {
            await helper.WriteLock.WaitAsync(ct);
            try { await PipeFraming.WriteAsync(helper.Pipe, new PipeMessage { Id = id, Req = request }, ct); }
            finally { helper.WriteLock.Release(); }

            using var reg = ct.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        catch (IOException) { return Response.Fail("The user helper disconnected."); }
        finally { helper.Pending.TryRemove(id, out _); }
    }
}
