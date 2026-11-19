// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using ClassroomManager.Client;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-134 over a real connection: what the Host shows ends with the error code, whether the Host or the Client knew the error.</summary>
[Collection("KeyRefusalPause")]
public sealed class ErrorIdEndToEndTests : IDisposable
{
    const string Key = "synthetic-error-id-key";

    public ErrorIdEndToEndTests() => KeyRefusalPause.ClearAll();
    public void Dispose() => KeyRefusalPause.ClearAll();

    static async Task<T> WithClientAsync<T>(Func<ClientInfo, CancellationToken, Task<T>> test)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var server = new CommandServer(new ClientConfig { UseTls = false }, new KeyStore(Key, []),
            (request, _, _, _) => Task.FromResult(request.Cmd == Cmd.SelfUpdate
                ? Response.Fail(ErrorCatalog.UpdateNotNewer, "Uploaded build (v1.2.0.417) is not newer than the running client (v1.2.0.417). Pass force to push it anyway.")
                : Response.Fail("Registry key not found.")),
            new PreAuthGate(new ConnectionLimits { FailuresBeforeBackoff = 100 }));
        listener.Start();
        var accepting = Task.Run(async () =>
        {
            try { while (!stop.IsCancellationRequested) _ = server.HandleAsync(await listener.AcceptTcpClientAsync(stop.Token), stop.Token); }
            catch (OperationCanceledException) { }
        });
        try { return await test(new ClientInfo { Address = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port }, stop.Token); }
        finally { stop.Cancel(); await accepting; listener.Stop(); }
    }

    [Fact]
    public async Task A_wrong_key_ends_with_its_code_and_keeps_its_type()
    {
        var error = await WithClientAsync((info, ct) => Assert.ThrowsAsync<AuthException>(() =>
            ClientSession.OpenAsync(info, new HostSettings { UseTls = false, Psk = "synthetic-other-key" }, ct)));

        Assert.EndsWith("(error code 020001)", error.Message);
    }

    [Fact]
    public async Task A_refusal_the_Client_sent_with_its_code_keeps_that_code()
    {
        var error = await WithClientAsync(async (info, ct) =>
        {
            using var session = await ClientSession.OpenAsync(info, new HostSettings { UseTls = false, Psk = Key }, ct);
            return await Assert.ThrowsAsync<RemoteException>(() => session.CallAsync(Cmd.SelfUpdate, ct: ct));
        });

        Assert.StartsWith("Uploaded build (v1.2.0.417) is not newer", error.Message);
        Assert.EndsWith("(error code 070001)", error.Message);
        Assert.Equal("070001", error.Payload[ErrorCatalog.PayloadKey]?.GetValue<string>());
    }

    [Fact]
    public async Task A_refusal_without_a_code_is_one_the_computer_could_not_do()
    {
        var error = await WithClientAsync(async (info, ct) =>
        {
            using var session = await ClientSession.OpenAsync(info, new HostSettings { UseTls = false, Psk = Key }, ct);
            return await Assert.ThrowsAsync<RemoteException>(() => session.CallAsync(Cmd.RegistryList, ct: ct));
        });

        Assert.Equal("Registry key not found. (error code 0A0001)", error.Message);
    }
}
