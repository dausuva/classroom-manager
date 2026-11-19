// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;

namespace ClassroomManager.Shared.Tests;

/// <summary>
/// A pair of connected, full-duplex streams for testing SecureChannel without any real network
/// or a fake stream implementation - a loopback TCP connection is the simplest thing that behaves
/// exactly like the sockets SecureChannel actually runs over in production.
/// </summary>
sealed class LoopbackStreamPair : IDisposable
{
    public NetworkStream Server { get; }
    public NetworkStream Client { get; }

    readonly TcpClient _serverSide;
    readonly TcpClient _clientSide;

    LoopbackStreamPair(TcpClient serverSide, TcpClient clientSide)
    {
        _serverSide = serverSide;
        _clientSide = clientSide;
        Server = serverSide.GetStream();
        Client = clientSide.GetStream();
    }

    public static async Task<LoopbackStreamPair> CreateAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var server = await acceptTask;
            return new LoopbackStreamPair(server, client);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Closes the server-side socket right now. Use this after deliberately failing a handshake
    /// on the server side to mirror what the real <c>CommandServer</c> does (its `using (tcp)`
    /// disposes the connection the moment authentication fails) - otherwise the client-side read
    /// that's waiting for a reply blocks until the OS eventually times the idle socket out, which
    /// can take minutes instead of milliseconds.
    /// </summary>
    public void CloseServerSocket() => _serverSide.Close();

    public void Dispose()
    {
        Server.Dispose();
        Client.Dispose();
        _serverSide.Dispose();
        _clientSide.Dispose();
    }
}
