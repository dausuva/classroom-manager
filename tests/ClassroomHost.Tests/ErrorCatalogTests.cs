// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>
/// T-134: how a failure becomes an error id, and how the id travels. The catalogue itself (ids, categories, the reference page) is checked in the shared library's
/// tests, <c>ErrorCatalogContractTests</c>, where the mutation test looks.
/// </summary>
public sealed class ErrorCatalogTests
{
    [Fact]
    public void A_message_gets_its_id_once_and_it_can_be_read_back()
    {
        var once = ErrorIds.WithId("Authentication failed.", ErrorCatalog.WrongKey);
        Assert.Equal("Authentication failed. (error code 020001)", once);
        Assert.Equal(once, ErrorIds.WithId(once, ErrorCatalog.Unexpected));   // never two ids
        Assert.True(ErrorIds.TrySplit(once, out var message, out var id));
        Assert.Equal(("Authentication failed.", "020001"), (message, id));
        Assert.False(ErrorIds.TrySplit("No id (error code 12345)", out _, out _));
        Assert.False(ErrorIds.TrySplit(null, out _, out _));
    }

    static RemoteException Remote(string message, JsonObject? data = null) => new(message, data ?? new JsonObject());

    public static TheoryData<string, Exception> Cases => new()
    {
        { "040004", new StepUpDeniedException("Not sent: you did not allow the action \"run a command\".") },
        { "050001", new UnsupportedFeatureException("x.y", "1.18", new JsonObject()) },
        { "040001", Remote("Not allowed with this key", new JsonObject { ["code"] = "forbidden" }) },
        { "040002", Remote("Too many requests", new JsonObject { ["code"] = "throttled" }) },
        { "020003", Remote("The access key for this connection was revoked", new JsonObject { ["code"] = "key_revoked" }) },
        { "040006", Remote("anything", new JsonObject { ["code"] = "forbidden", [ErrorCatalog.PayloadKey] = "040006" }) },   // the Client's own id wins
        { "040003", Remote("Refused (power.action): the host and this client are the same computer. Actions that could ...") },
        { "050001", Remote("Unknown command: x.y") },
        { "0A0001", Remote("Registry key not found.") },
        { "020002", KeyRefusalPause.NotTried(DateTime.UtcNow.AddMinutes(3)) },
        { "020002", new AuthException("Authentication failed (wrong shared key?). A later try was dropped without an answer: the computer may be ignoring this PC for a few minutes after refused keys; wait about five minutes.") },
        { "020004", new AuthException("Server failed to prove it knows the shared key.") },
        { "020001", new AuthException("Authentication failed (wrong shared key?).") },
        { "030001", new AuthenticationException("TLS endpoint trust is missing or invalid. Independently verify and enroll this computer's certificate SHA-256 before connecting. Do not turn TLS off.") },
        { "030002", new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback.") },
        { "030003", new AuthenticationException("Authentication failed because the remote party sent a TLS alert: 'ProtocolVersion'.") },
        { "010001", new SocketException((int)SocketError.ConnectionRefused) },
        { "010002", new SocketException((int)SocketError.HostUnreachable) },
        { "010002", new TimeoutException("Connection timed out.") },
        { "010003", new TimeoutException("The client stopped answering.") },
        { "010004", new IOException("Unable to read data from the transport connection.") },
        { "010001", new IOException("Unable to write data", new SocketException((int)SocketError.ConnectionRefused)) },
        { "020005", new InvalidOperationException("No host key is configured. Open Lab keys and add the key for this classroom.") },
        { "040005", new InvalidOperationException("Updating the client. Other actions are paused to avoid interrupting it.") },
        { "000001", new InvalidOperationException("Something nobody planned for.") },
        { "000001", new NullReferenceException() },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_kind_of_failure_gets_its_id(string expected, Exception ex)
    {
        Assert.Equal(expected, ErrorClassifier.Classify(ex).Id);
        Assert.EndsWith($"(error code {expected})", ErrorClassifier.Describe(ex));
    }

    [Fact]
    public void Tagging_keeps_the_type_callers_catch_and_the_start_of_the_message_they_compare()
    {
        var auth = ErrorClassifier.WithId(new AuthException("Authentication failed (wrong shared key?)."));
        Assert.IsType<AuthException>(auth);
        Assert.StartsWith("Authentication failed (wrong shared key?).", auth.Message);

        var remote = (RemoteException)ErrorClassifier.WithId(Remote("This computer has no access key named 'x'.", new JsonObject { ["code"] = "y" }));
        Assert.StartsWith("This computer has no access key named", remote.Message);
        Assert.Equal("y", remote.Payload["code"]?.GetValue<string>());

        var pause = ErrorClassifier.WithId(KeyRefusalPause.NotTried(DateTime.UtcNow.AddMinutes(2)));
        Assert.True(KeyRefusalPause.IsPauseMessage(pause.Message));

        var socket = new SocketException((int)SocketError.ConnectionRefused);
        Assert.Same(socket, ErrorClassifier.WithId(socket));                      // cannot carry it; Describe adds it where it is shown
        var cancelled = new OperationCanceledException();
        Assert.Same(cancelled, ErrorClassifier.WithId(cancelled));                // a cancellation stays a cancellation
    }

    [Fact]
    public void In_Lithuanian_the_message_and_the_id_are_both_translated()
    {
        var lithuanian = Loc.BuiltIn(Loc.Lithuanian);
        var shown = lithuanian.Translate("That is already the current key. (error code 020001)");

        Assert.Equal(lithuanian.Translate("That is already the current key.") + lithuanian.Translate(" (error code 020001)"), shown);
        Assert.Contains("020001", shown);
        Assert.DoesNotContain("(error code", shown);
    }

    [Fact]
    public void The_Client_sends_its_ids_with_the_refusals_it_knows()
    {
        // Read from the sources, because these refusals need a Client service to produce.
        var client = Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomClient");
        string Source(string file) => File.ReadAllText(Path.Combine(client, file));
        Assert.Contains("ErrorCatalog.SameComputer", Source("CommandRouter.cs"));
        Assert.Contains("ErrorCatalog.NotAllowedForRole", Source("CommandRouter.cs"));
        Assert.Contains("ErrorCatalog.Throttled", Source("CommandServer.cs"));
        Assert.Contains("ErrorCatalog.KeyRevoked", Source("CommandServer.cs"));
        var update = Source(Path.Combine("Handlers", "UpdateHandler.cs"));
        Assert.True(Regex.Matches(update, @"Response\.Fail\(ErrorCatalog\.").Count >= 9, "every refusal of an update before it is installed carries an id");
    }
}
