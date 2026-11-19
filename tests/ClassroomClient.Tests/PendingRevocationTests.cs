// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-189: an access key removed while a computer was off is removed there when it next answers.</summary>
[Collection("KeyRefusalPause")]
public sealed class PendingRevocationTests
{
    const string Admin = "synthetic-admin-key-0001";
    const string Teacher = "synthetic-teacher-key-0002";
    static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    sealed class FakeClient : IAsyncDisposable
    {
        readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly Task _accepting;
        public KeyStore Keys { get; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public CancellationToken Token => _stop.Token;

        public FakeClient(bool holdsKey)
        {
            Keys = new KeyStore(Admin, holdsKey ? [new ClientAccessKey { Name = "Cover teacher", Role = "Teacher", Psk = Teacher }] : []);
            var handler = new KeyHandler(Keys, _ => { }, _ => { });
            var server = new CommandServer(new ClientConfig { UseTls = false }, Keys, (request, _, caller, _) =>
                Task.FromResult(request.Cmd == Cmd.AccessKeysRemove ? handler.RemoveAccessKey(request.Args, caller) : Response.Success()),
                new PreAuthGate(new ConnectionLimits { FailuresBeforeBackoff = 100 }));
            _listener.Start();
            _accepting = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                        _ = server.HandleAsync(tcp, _stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        public ClientInfo Info(string clientId = "") => new() { Address = "127.0.0.1", Port = Port, ClientId = clientId };

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _accepting;
            _listener.Stop();
            _stop.Dispose();
        }
    }

    static HostSettings Settings() => new() { UseTls = false, Psk = Admin };

    [Fact]
    public void A_removal_is_recorded_once_per_key_and_computer_and_cancelled_when_the_name_is_used_again()
    {
        var settings = Settings();
        var off = new ClientInfo { Address = "10.0.0.5", ClientId = "abc" };
        var noId = new ClientInfo { Address = "10.0.0.6" };

        Assert.Equal(2, KeyRevocations.Record(settings, "Cover teacher", [off, noId], Now));
        Assert.Equal(0, KeyRevocations.Record(settings, "cover teacher", [off], Now));
        Assert.Equal(["Cover teacher"], KeyRevocations.For(settings, off));
        Assert.Equal(["Cover teacher"], KeyRevocations.For(settings, noId));
        Assert.Empty(KeyRevocations.For(settings, new ClientInfo { Address = "10.0.0.7" }));

        Assert.Equal(2, KeyRevocations.Cancel(settings, "COVER TEACHER"));
        Assert.Equal(0, KeyRevocations.Waiting(settings));
    }

    [Fact]
    public void A_computer_is_recognised_by_its_client_id_after_its_address_changed_and_by_address_before_the_id_is_known()
    {
        var settings = Settings();
        KeyRevocations.Record(settings, "Cover teacher", [new ClientInfo { Address = "10.0.0.5", ClientId = "abc" }], Now);
        KeyRevocations.Record(settings, "Cover teacher", [new ClientInfo { Address = "10.0.0.6" }], Now);

        Assert.Single(KeyRevocations.For(settings, new ClientInfo { Address = "10.0.0.99", ClientId = "ABC" }));
        Assert.Single(KeyRevocations.For(settings, new ClientInfo { Address = "10.0.0.6", ClientId = "learned-later" }));
    }

    [Fact]
    public void The_settings_keep_the_waiting_removals_when_saved_and_loaded()
    {
        var settings = Settings();
        KeyRevocations.Record(settings, "Cover teacher", [new ClientInfo { Address = "10.0.0.5", ClientId = "abc" }], Now);

        var copy = System.Text.Json.JsonSerializer.Deserialize<HostSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;

        var entry = Assert.Single(copy.PendingRevocations);
        Assert.Equal(("Cover teacher", "abc", Now), (entry.Name, entry.Computer, entry.RequestedUtc));
    }

    [Fact]
    public async Task A_computer_that_answers_later_gets_the_key_removed_and_the_entry_is_finished()
    {
        await using var computer = new FakeClient(holdsKey: true);
        var settings = Settings();
        var info = computer.Info("abc");
        KeyRevocations.Record(settings, "Cover teacher", [info], Now);
        using (var before = await ClientSession.OpenWithKeyAsync(info, settings, Teacher, computer.Token)) { }   // the key works while it is still there

        using var session = await ClientSession.OpenAsync(info, settings, computer.Token);
        var (done, failed) = await KeyRevocations.ApplyAsync(settings, info, session, computer.Token);

        Assert.Equal(["Cover teacher"], done);
        Assert.Empty(failed);
        Assert.Equal(0, KeyRevocations.Waiting(settings));
        await Assert.ThrowsAnyAsync<Exception>(() => ClientSession.OpenWithKeyAsync(info, settings, Teacher, computer.Token));
    }

    [Fact]
    public async Task A_computer_that_never_had_the_key_counts_as_done_and_other_waiting_entries_stay()
    {
        await using var computer = new FakeClient(holdsKey: false);
        var settings = Settings();
        var info = computer.Info("abc");
        KeyRevocations.Record(settings, "Cover teacher", [info], Now);
        KeyRevocations.Record(settings, "Cover teacher", [new ClientInfo { Address = "10.0.0.5", ClientId = "other" }], Now);

        using var session = await ClientSession.OpenAsync(info, settings, computer.Token);
        var (done, _) = await KeyRevocations.ApplyAsync(settings, info, session, computer.Token);

        Assert.Equal(["Cover teacher"], done);
        Assert.Equal(1, KeyRevocations.Waiting(settings));   // the other computer still owes its removal
    }
}
