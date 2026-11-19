// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

public sealed class KeyRevocationTests
{
    const string Primary = "synthetic-primary-key";
    const string Teacher = "synthetic-teacher-key";
    const string Replacement = "synthetic-replacement-key";

    static KeyStore Store() => new(Primary, [new ClientAccessKey { Name = "Teacher", Role = "Teacher", Psk = Teacher }]);

    [Fact]
    public void Unchanged_credentials_keep_identity_but_removed_objects_never_return()
    {
        var keys = Store();
        var primary = keys.Primary;
        var teacher = keys.Named.Single();
        keys.Set(Primary);
        Assert.Same(primary, keys.Primary);
        Assert.False(keys.IsCurrent(new AccessKey("Teacher", OperatorRole.Teacher, Teacher)));
        keys.SetNamed([new AccessKey("Teacher", OperatorRole.Teacher, Teacher)]);
        Assert.Same(primary, keys.Primary);
        Assert.Same(teacher, keys.Named.Single());
        keys.SetNamed([]);
        keys.SetNamed([teacher]);
        Assert.False(keys.IsCurrent(teacher));
        Assert.True(keys.IsCurrent(primary));
        keys.Set(Replacement);
        keys.Set(Primary);
        Assert.False(keys.IsCurrent(primary));
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("named")]
    public void A_failed_persist_does_not_revoke_the_current_credential(string kind)
    {
        var keys = Store();
        var before = keys.Current.ToArray();
        var handler = new KeyHandler(keys, _ => throw new IOException("Synthetic failure"), _ => throw new IOException("Synthetic failure"));
        Assert.Throws<IOException>(() => kind == "primary"
            ? handler.Set(new JsonObject { ["key"] = Replacement })
            : handler.RemoveAccessKey(new JsonObject { ["name"] = "Teacher" }, CallerIdentity.Service));
        Assert.All(before, key => Assert.True(keys.IsCurrent(key)));
    }

    [Fact]
    public void The_key_epoch_is_kept_only_when_the_host_sends_one_and_a_failed_write_of_it_does_not_undo_the_key()
    {
        var keys = new KeyStore(Primary, epoch: 4);
        var saved = new List<int>();
        var handler = new KeyHandler(keys, _ => { }, _ => { }, saveEpoch: saved.Add);

        Assert.True(handler.Set(new JsonObject { ["key"] = Replacement }).Ok);          // no epoch sent: the computer keeps its own
        Assert.Equal(4, keys.Epoch);
        Assert.Empty(saved);

        var answer = handler.Set(new JsonObject { ["key"] = "synthetic-key-number-two", ["epoch"] = 5 });
        Assert.True(answer.Ok);
        Assert.Equal(5, keys.Epoch);
        Assert.Equal([5], saved);
        Assert.False(handler.Set(new JsonObject { ["key"] = "synthetic-key-number-three", ["epoch"] = -1 }).Ok);

        var failing = new KeyHandler(keys, _ => { }, _ => { }, saveEpoch: _ => throw new IOException("Synthetic failure"));
        Assert.True(failing.Set(new JsonObject { ["key"] = "synthetic-key-number-four", ["epoch"] = 6 }).Ok);
        Assert.Equal("synthetic-key-number-four", keys.Primary.Psk);
    }

    static KeyStore ScopedStore(string room) => new(Primary,
        [new ClientAccessKey { Name = "Room 410 teacher", Role = "Teacher", Psk = Teacher, Rooms = ["410"] }], room: room);

    [Theory]
    [InlineData("410", true)]
    [InlineData(" 410 ", true)]
    [InlineData("305", false)]
    [InlineData("", false)]
    public void A_key_limited_to_a_room_opens_only_computers_in_that_room_and_never_one_without_a_room(string room, bool opens)
    {
        var keys = ScopedStore(room);
        var scoped = keys.Named.Single();

        Assert.Equal(opens, keys.Current.Contains(scoped));
        Assert.Equal(opens, keys.IsCurrent(scoped));
        Assert.Contains(keys.Primary, keys.Current);   // the lab key is never limited to a room
    }

    [Fact]
    public void Adding_a_key_for_rooms_labels_the_computer_and_the_answer_lists_the_rooms()
    {
        var keys = new KeyStore(Primary);
        string? savedRoom = null;
        List<ClientAccessKey>? savedKeys = null;
        var handler = new KeyHandler(keys, _ => { }, list => savedKeys = list.ToList(), saveRoom: room => savedRoom = room);

        var answer = handler.SetAccessKey(new JsonObject
        {
            ["name"] = "Room 410 teacher", ["role"] = "Teacher", ["key"] = Teacher, ["rooms"] = "410; 411", ["computerRoom"] = "410",
        }, CallerIdentity.Service);

        Assert.True(answer.Ok);
        Assert.Equal("410", keys.Room);
        Assert.Equal("410", savedRoom);
        Assert.Equal(["410", "411"], savedKeys!.Single().Rooms);
        var listed = (answer.Data!["keys"] as JsonArray)!.OfType<JsonObject>().Single(item => item["name"]!.GetValue<string>() == "Room 410 teacher");
        Assert.Equal(["410", "411"], (listed["rooms"] as JsonArray)!.Select(room => room!.GetValue<string>()));
        Assert.Contains(keys.Named.Single(), keys.Current);
    }

    [Fact]
    public void A_key_without_rooms_lists_none_and_too_many_rooms_are_refused()
    {
        var keys = new KeyStore(Primary);
        var handler = new KeyHandler(keys, _ => { }, _ => { }, saveRoom: _ => { });

        var plain = handler.SetAccessKey(new JsonObject { ["name"] = "Anyone", ["role"] = "Teacher", ["key"] = Teacher }, CallerIdentity.Service);
        var listed = (plain.Data!["keys"] as JsonArray)!.OfType<JsonObject>().Single(item => item["name"]!.GetValue<string>() == "Anyone");
        Assert.Null(listed["rooms"]);

        var tooMany = string.Join(",", Enumerable.Range(1, 21).Select(i => $"R{i}"));
        Assert.False(handler.SetAccessKey(new JsonObject { ["name"] = "Many", ["role"] = "Teacher", ["key"] = Replacement, ["rooms"] = tooMany }, CallerIdentity.Service).Ok);
    }

    [Fact]
    public async Task Concurrent_named_changes_do_not_lose_each_other()
    {
        var keys = Store();
        var handler = new KeyHandler(keys, _ => { }, _ => { });
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            handler.SetAccessKey(new JsonObject { ["name"] = $"Synthetic-{i}", ["role"] = "Teacher", ["key"] = $"synthetic-test-key-{i}" }, CallerIdentity.Service))));
        Assert.All(results, response => Assert.True(response.Ok));
        Assert.Equal(21, keys.Named.Count);
    }

    [Fact]
    public async Task Removed_named_session_is_refused_before_dispatch_and_closed_while_unrelated_session_survives()
    {
        var keys = Store();
        await using var server = new TestServer(keys);
        using var admin = await server.Connect(Primary);
        using var teacher = await server.Connect(Teacher);
        await teacher.CallAsync(Cmd.Ping, ct: server.Token);
        keys.SetNamed([]);
        var before = server.Dispatches;
        var failure = await Assert.ThrowsAsync<RemoteException>(() => teacher.CallAsync(Cmd.Ping, ct: server.Token));
        Assert.Equal("key_revoked", failure.Payload["code"]?.GetValue<string>());
        Assert.Equal(before, server.Dispatches);
        await Assert.ThrowsAnyAsync<IOException>(() => teacher.CallAsync(Cmd.Ping, ct: server.Token));
        Assert.Equal("Admin", (await admin.CallAsync(Cmd.Ping, ct: server.Token))["role"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("role")]
    [InlineData("demote")]
    [InlineData("readd")]
    public async Task Named_credential_changes_never_reuse_an_old_session(string change)
    {
        var keys = change == "demote"
            ? new KeyStore(Primary, [new ClientAccessKey { Name = "Teacher", Role = "Admin", Psk = Teacher }])
            : Store();
        await using var server = new TestServer(keys);
        using var old = await server.Connect(Teacher);
        var original = keys.Named.Single();
        if (change == "readd") keys.SetNamed([]);
        keys.SetNamed([new AccessKey("Teacher", change == "role" ? OperatorRole.Admin : OperatorRole.Teacher,
            change == "replace" ? Replacement : Teacher)]);
        // No request was sent before the change: also covers a revoked handshake snapshot.
        Assert.Equal("key_revoked", (await Assert.ThrowsAsync<RemoteException>(() => old.CallAsync(Cmd.Ping, ct: server.Token))).Payload["code"]?.GetValue<string>());
        using var current = await server.Connect(change == "replace" ? Replacement : Teacher);
        Assert.Equal(change == "role" ? "Admin" : "Teacher", (await current.CallAsync(Cmd.Ping, ct: server.Token))["role"]?.GetValue<string>());
        Assert.False(keys.IsCurrent(original));
    }

    [Fact]
    public async Task Rotation_acknowledges_the_change_before_refusing_the_next_old_session_request()
    {
        var keys = Store();
        await using var server = new TestServer(keys);
        using var previous = await server.Connect(Primary);
        await KeyRotation.ApplyAndVerifyAsync(server.Client, server.Settings, previous, Replacement);
        Assert.Equal(Replacement, keys.Primary.Psk);
        Assert.Equal("key_revoked", (await Assert.ThrowsAsync<RemoteException>(() => previous.CallAsync(Cmd.Ping, ct: server.Token))).Payload["code"]?.GetValue<string>());
        using var unaffected = await server.Connect(Teacher);
        Assert.Equal("Teacher", (await unaffected.CallAsync(Cmd.Ping, ct: server.Token))["role"]?.GetValue<string>());
    }

    [Fact]
    public async Task Host_rotation_recovery_authenticates_with_the_new_key_and_verifies_the_restored_key()
    {
        var keys = Store();
        await using var server = new TestServer(keys) { FailNextReplacementPing = true };
        using var previous = await server.Connect(Primary);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => KeyRotation.ApplyAndVerifyAsync(server.Client, server.Settings, previous, Replacement));
        Assert.Contains("the computer keeps its previous key", error.Message);
        Assert.Equal(Primary, keys.Primary.Psk);
        Assert.Equal("key_revoked", (await Assert.ThrowsAsync<RemoteException>(() => previous.CallAsync(Cmd.Ping, ct: server.Token))).Payload["code"]?.GetValue<string>());
        using var restored = await server.Connect(Primary);
        Assert.True((await restored.CallAsync(Cmd.Ping, ct: server.Token))["role"]?.GetValue<string>() == "Admin");
    }

    [Fact]
    public async Task A_request_admitted_before_removal_may_finish_but_the_next_one_is_refused()
    {
        var keys = Store();
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new TestServer(keys, async (_, _) =>
        {
            admitted.TrySetResult();
            await release.Task;
            return Response.Success();
        });
        using var teacher = await server.Connect(Teacher);
        var first = teacher.CallAsync(Cmd.Ping, ct: server.Token);
        try { await admitted.Task.WaitAsync(server.Token); keys.SetNamed([]); }
        finally { release.TrySetResult(); }
        await first;
        Assert.Equal("key_revoked", (await Assert.ThrowsAsync<RemoteException>(() => teacher.CallAsync(Cmd.Ping, ct: server.Token))).Payload["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task Failed_recovery_does_not_claim_that_the_previous_key_was_restored()
    {
        var keys = Store();
        await using var server = new TestServer(keys) { FailNextReplacementPing = true, FailRestoration = true };
        using var previous = await server.Connect(Primary);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => KeyRotation.ApplyAndVerifyAsync(server.Client, server.Settings, previous, Replacement));
        Assert.Contains("the previous key could not be put back", error.Message);
        Assert.Equal(Replacement, keys.Primary.Psk);
        using var current = await server.Connect(Replacement);
        Assert.Equal("Admin", (await current.CallAsync(Cmd.Ping, ct: server.Token))["role"]?.GetValue<string>());
    }

    // Real CommandServer/SecureChannel/Host ClientSession, over loopback with synthetic keys.
    // No real CommandRouter, staging constructor, service, config file or TLS store is touched.
    sealed class TestServer : IAsyncDisposable
    {
        readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly ConcurrentBag<Task> _connections = [];
        readonly Task _accepting;
        int _dispatches;
        public int Dispatches => Volatile.Read(ref _dispatches);
        public bool FailNextReplacementPing { get; set; }
        public bool FailRestoration { get; set; }
        public ClientInfo Client { get; }
        public HostSettings Settings { get; } = new() { UseTls = false };
        public CancellationToken Token => _stop.Token;

        public TestServer(KeyStore keys, Func<Request, CallerIdentity, Task<Response>>? handler = null)
        {
            _listener.Start();
            Client = new ClientInfo { Address = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port };
            var server = new CommandServer(new ClientConfig { UseTls = false }, keys, async (request, _, caller, _) =>
            {
                Interlocked.Increment(ref _dispatches);
                if (handler is not null) return await handler(request, caller);
                if (request.Cmd == Cmd.KeySet)
                {
                    var next = request.Args["key"]!.GetValue<string>();
                    if (FailRestoration && keys.Primary.Psk == Replacement && next == Primary) return Response.Fail("Synthetic restoration failure");
                    keys.Set(next);
                    return Response.Success();
                }
                if (request.Cmd == Cmd.Ping && keys.Primary.Psk == Replacement && FailNextReplacementPing)
                { FailNextReplacementPing = false; return Response.Fail("Synthetic verification failure"); }
                return Response.Success(new JsonObject { ["role"] = caller.Role.ToString() });
            });
            _accepting = AcceptAsync(server);
        }

        async Task AcceptAsync(CommandServer server)
        {
            try
            {
                while (!Token.IsCancellationRequested)
                    _connections.Add(server.HandleAsync(await _listener.AcceptTcpClientAsync(Token), Token));
            }
            catch (OperationCanceledException) { }
        }

        public Task<ClientSession> Connect(string key) => ClientSession.OpenWithKeyAsync(Client, Settings, key, Token);

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _accepting;
            _listener.Stop();
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }
    }
}
