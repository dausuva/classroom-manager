// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.ComponentModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using ClassroomManager.Client;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

public sealed class PipePeerIdentityTests
{
    static readonly HelperIdentity Alice = new("S-1-5-21-100-200-300-1001", "LAB\\Alice", 3);

    [Fact]
    public void Hello_name_is_not_trusted_and_the_windows_session_must_match()
    {
        PipePeerIdentity.ValidateHello(new PipeMessage { User = "Administrator", Session = 3 }, Alice);
        Assert.Throws<SecurityException>(() => PipePeerIdentity.ValidateHello(new PipeMessage { User = "Alice", Session = 4 }, Alice));
    }

    [Fact]
    public void Token_sid_must_match_the_signed_in_session_owner()
    {
        PipePeerIdentity.ValidateSessionOwner(Alice, Alice.Sid);
        Assert.Throws<SecurityException>(() => PipePeerIdentity.ValidateSessionOwner(Alice, "S-1-5-21-100-200-300-1002"));
        Assert.Throws<SecurityException>(() => PipePeerIdentity.ValidateSessionOwner(Alice, null));
        Assert.Throws<SecurityException>(() => PipePeerIdentity.ValidateSessionOwner(Alice with { Session = 0 }, Alice.Sid));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Noninteractive_or_invalid_windows_sessions_are_rejected(int session) =>
        Assert.Throws<SecurityException>(() => PipePeerIdentity.ValidateHello(new PipeMessage { Session = session }, Alice with { Session = session }));

    [Fact]
    public void Commands_responses_and_nonzero_ids_are_not_registration_messages()
    {
        Assert.Throws<SecurityException>(() => PipePeerIdentity.ValidateHello(new PipeMessage { Id = 1, Session = 3 }, Alice));
        Assert.Throws<SecurityException>(() => PipePeerIdentity.ValidateHello(new PipeMessage { Session = 3, Req = new Request { Cmd = Cmd.Ping } }, Alice));
        Assert.Throws<SecurityException>(() => PipePeerIdentity.ValidateHello(new PipeMessage { Session = 3, Res = Response.Success() }, Alice));
    }

    [Theory]
    [InlineData(0, 4, 12)]
    [InlineData(11, 4, 12)]
    [InlineData(12, 4, 0)]
    [InlineData(12, 1, 12)]
    [InlineData(12, 2, 12)]
    [InlineData(12, 3, 12)]
    [InlineData(12, 7, 12)]
    public void Wrong_or_unreliable_service_identity_is_rejected(uint pipePid, uint state, uint servicePid) =>
        Assert.Throws<SecurityException>(() => PipePeerIdentity.ValidateService(pipePid, state, servicePid));

    [Fact]
    public void Only_the_running_service_process_matches() => PipePeerIdentity.ValidateService(12, 4, 12);

    static async Task WithPipe(TokenImpersonationLevel level, Func<NamedPipeServerStream, NamedPipeClientStream, Task> test)
    {
        var name = "cm-peer-test-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, level);
        var accept = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await accept;
        // Named pipe impersonation uses the security context of the last message read.
        var read = PipeFraming.ReadAsync(server, timeout.Token);
        await PipeFraming.WriteAsync(client, new PipeMessage { User = "forged", Session = int.MaxValue }, timeout.Token);
        await read;
        await test(server, client);
    }

    [Fact]
    public async Task Real_pipe_identity_comes_from_windows_not_the_frame()
    {
        await WithPipe(TokenImpersonationLevel.Identification, (server, _) =>
        {
            using var current = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            var expectedSession = process.SessionId;
            var peer = PipePeerIdentity.ReadHelper(server);
            using var stillImpersonating = WindowsIdentity.GetCurrent(ifImpersonating: true);
            Assert.Null(stillImpersonating);
            Assert.Equal(current.User!.Value, peer.Sid);
            Assert.Equal(current.Name, peer.Name);
            Assert.Equal(expectedSession, peer.Session);
            Assert.Throws<SecurityException>(() => PipePeerIdentity.ValidateHello(new PipeMessage { Session = int.MaxValue }, peer));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Anonymous_pipe_client_cannot_supply_verified_identity()
    {
        await WithPipe(TokenImpersonationLevel.Anonymous, (server, _) =>
        {
            Assert.NotNull(Record.Exception(() => PipePeerIdentity.ReadHelper(server)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Unrelated_pipe_server_is_rejected_without_sending_it_requests()
    {
        await WithPipe(TokenImpersonationLevel.Identification, (_, client) =>
        {
            var error = Record.Exception(() => PipePeerIdentity.VerifyService(client));
            Assert.True(error is SecurityException or Win32Exception, error?.ToString() ?? "An unrelated server was trusted.");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Bridge_does_not_register_a_forged_session()
    {
        await WithPipe(TokenImpersonationLevel.Identification, async (server, client) =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var bridge = new UserBridge();
            var serving = bridge.ServeHelperAsync(server, timeout.Token);
            await PipeFraming.WriteAsync(client, new PipeMessage { Session = int.MaxValue, User = "forged" }, timeout.Token);
            await serving.WaitAsync(timeout.Token);
            Assert.Empty(bridge.ConnectedSessions);
            Assert.Null(bridge.CurrentUser);
        });
    }

    [Fact]
    public async Task Registration_frame_limit_is_checked_before_reading_a_body()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 4097);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeFraming.ReadAsync(stream, CancellationToken.None, maxFrameBytes: 4096));
    }
}
