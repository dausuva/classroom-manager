// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassroomManager.Client;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomClient.Tests;

/// <summary>T-184 over a real UDP socket: a flood of discovery questions gets only the rationed answers.</summary>
[Collection("UdpDiscovery")]
public sealed class DiscoveryRationingTests
{
    static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    static async Task<int> AskAsync(int port, int questions, TimeSpan listenFor)
    {
        using var asker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var question = Encoding.UTF8.GetBytes(Protocol.DiscoverMagic);
        for (var i = 0; i < questions; i++) await asker.SendAsync(question, new IPEndPoint(IPAddress.Loopback, port));

        var answers = 0;
        using var stop = new CancellationTokenSource(listenFor);
        try
        {
            while (true)
            {
                try { await asker.ReceiveAsync(stop.Token); answers++; }
                // Windows reports "nobody was listening yet" (ICMP port unreachable) on the asking socket; that is not an answer.
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { }
            }
        }
        catch (OperationCanceledException) { }
        return answers;
    }

    [Fact]
    public async Task A_flood_of_questions_gets_at_most_the_allowed_answers_and_a_single_question_is_still_answered()
    {
        var port = FreeUdpPort();
        var config = new ClientConfig { UdpPort = port, ClientId = "test-client", Discovery = new DiscoveryLimits { PerSourcePerSecond = 3, PerSourcePerMinute = 30 } };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var responder = Task.Run(() => new DiscoveryResponder(config).RunAsync(stop.Token));
        await AskAsync(port, questions: 1, TimeSpan.FromSeconds(0.5));   // wait until it is listening (this one is answered too)
        await Task.Delay(1100);                                           // and let that answer leave the first second

        var answers = await AskAsync(port, questions: 200, TimeSpan.FromSeconds(1.5));

        stop.Cancel();
        await responder;
        Assert.InRange(answers, 1, 4);                          // 3 in the first second, and a late one at most
    }

    [Fact]
    public async Task One_question_is_still_answered()
    {
        var port = FreeUdpPort();
        var config = new ClientConfig { UdpPort = port, ClientId = "test-client" };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var responder = Task.Run(() => new DiscoveryResponder(config).RunAsync(stop.Token));

        // The responder may still be binding on a busy machine: ask again until it answers, but one question per try.
        var answered = 0;
        for (var attempt = 0; attempt < 10 && answered == 0; attempt++)
            answered = await AskAsync(port, questions: 1, TimeSpan.FromSeconds(1));
        Assert.Equal(1, answered);

        stop.Cancel();
        await responder;
    }
}
