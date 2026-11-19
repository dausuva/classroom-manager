// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text;
using Xunit;

namespace ClassroomManager.Shared.Tests;

public class SecureChannelTests
{
    static readonly byte[] Psk = Encoding.UTF8.GetBytes("test-shared-secret");

    // Every handshake/frame call below gets a hard deadline. SecureChannel's reads have no
    // built-in timeout (by design - it's a long-lived connection), so a test that gets the
    // protocol wrong in a way that leaves one side waiting for a reply that will never come
    // must fail fast and loudly instead of hanging until some OS-level socket timeout fires
    // (which can take minutes and made an earlier version of this test suite look "stuck").
    static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    [Fact]
    public async Task Handshake_succeeds_with_matching_keys_and_exchanges_a_message()
    {
        using var pair = await LoopbackStreamPair.CreateAsync();
        var ct = Timeout();

        var acceptTask = SecureChannel.AcceptAsync(pair.Server, Psk, ct);
        var connectTask = SecureChannel.ConnectAsync(pair.Client, Psk, ct);
        await Task.WhenAll(acceptTask, connectTask);

        using var server = acceptTask.Result;
        using var client = connectTask.Result;

        var payload = Encoding.UTF8.GetBytes("hello from the host");
        await client.SendAsync(payload, ct);
        var received = await server.ReceiveAsync(ct);

        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task Round_trips_a_full_file_chunk_when_frame_writes_are_segmented()
    {
        using var pair = await LoopbackStreamPair.CreateAsync();
        var ct = Timeout();
        var acceptTask = SecureChannel.AcceptAsync(pair.Server, Psk, ct);
        var connectTask = SecureChannel.ConnectAsync(pair.Client, Psk, ct);
        await Task.WhenAll(acceptTask, connectTask);
        using var server = acceptTask.Result;
        using var client = connectTask.Result;
        var payload = Enumerable.Range(0, Protocol.ChunkSize).Select(index => (byte)(index % 251)).ToArray();

        var receive = server.ReceiveAsync(ct);
        await client.SendAsync(payload, ct);

        Assert.Equal(payload, await receive);
    }

    [Fact]
    public async Task Handshake_fails_when_the_client_uses_the_wrong_key()
    {
        using var pair = await LoopbackStreamPair.CreateAsync();
        var ct = Timeout();

        var acceptTask = SecureChannel.AcceptAsync(pair.Server, Psk, ct);
        var wrongKey = Encoding.UTF8.GetBytes("not-the-right-secret");
        var connectTask = SecureChannel.ConnectAsync(pair.Client, wrongKey, ct);

        // The server sees the bad MAC first and throws without ever sending a reply. The real
        // CommandServer closes the TCP connection at that point (its `using (tcp)`); mirror that
        // here so the client's pending read sees the connection drop (-> AuthException) instead
        // of blocking until the CancellationToken above eventually fires.
        await Assert.ThrowsAsync<AuthException>(() => acceptTask);
        pair.CloseServerSocket();

        await Assert.ThrowsAsync<AuthException>(() => connectTask);
    }

    [Fact]
    public async Task Round_trips_json_messages_in_both_directions()
    {
        using var pair = await LoopbackStreamPair.CreateAsync();
        var ct = Timeout();

        var acceptTask = SecureChannel.AcceptAsync(pair.Server, Psk, ct);
        var connectTask = SecureChannel.ConnectAsync(pair.Client, Psk, ct);
        await Task.WhenAll(acceptTask, connectTask);

        using var server = acceptTask.Result;
        using var client = connectTask.Result;

        var request = new Request { Cmd = Cmd.Ping, Level = Levels.System };
        await client.SendJsonAsync(request, ct);
        var receivedRequest = await server.ReceiveJsonAsync<Request>(ct);
        Assert.Equal(Cmd.Ping, receivedRequest.Cmd);
        Assert.Equal(Levels.System, receivedRequest.Level);

        var response = Response.Success();
        await server.SendJsonAsync(response, ct);
        var receivedResponse = await client.ReceiveJsonAsync<Response>(ct);
        Assert.True(receivedResponse.Ok);
    }

    [Fact]
    public async Task Rejects_a_frame_whose_mac_was_tampered_with_in_transit()
    {
        using var pair = await LoopbackStreamPair.CreateAsync();
        var ct = Timeout();
        var corruptingClientWire = new CorruptingStream(pair.Client);

        var acceptTask = SecureChannel.AcceptAsync(pair.Server, Psk, ct);
        var connectTask = SecureChannel.ConnectAsync(corruptingClientWire, Psk, ct);
        await Task.WhenAll(acceptTask, connectTask);

        using var server = acceptTask.Result;
        using var client = connectTask.Result;

        // Handshake already completed cleanly; arm corruption for the next frame only.
        corruptingClientWire.CorruptNextWrite = true;
        await client.SendAsync(Encoding.UTF8.GetBytes("tampered payload"), ct);

        await Assert.ThrowsAsync<AuthException>(() => server.ReceiveAsync(ct));
    }

    [Fact]
    public async Task Sequence_numbers_prevent_a_replayed_frame_from_being_accepted_twice()
    {
        using var pair = await LoopbackStreamPair.CreateAsync();
        var ct = Timeout();

        var acceptTask = SecureChannel.AcceptAsync(pair.Server, Psk, ct);
        var connectTask = SecureChannel.ConnectAsync(pair.Client, Psk, ct);
        await Task.WhenAll(acceptTask, connectTask);

        using var server = acceptTask.Result;
        using var client = connectTask.Result;

        await client.SendAsync(Encoding.UTF8.GetBytes("first"), ct);
        await client.SendAsync(Encoding.UTF8.GetBytes("second"), ct);

        var first = await server.ReceiveAsync(ct);
        var second = await server.ReceiveAsync(ct);

        Assert.Equal("first", Encoding.UTF8.GetString(first));
        Assert.Equal("second", Encoding.UTF8.GetString(second));
    }
}
