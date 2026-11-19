// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// Gives every failure the Host shows its stable error id (T-134). A Client that knows the error sends the id with its answer; for the rest
/// the kind of exception and the program's own message decide. The messages compared here are the program's own English sources, so the
/// classification does not depend on the language shown.
/// </summary>
public static class ErrorClassifier
{
    public static ErrorInfo Classify(Exception ex)
    {
        if (ErrorIds.TrySplit(ex.Message, out _, out var tagged) && ErrorCatalog.Find(tagged) is { } already) return already;
        var message = ex.Message;
        switch (ex)
        {
            case StepUpDeniedException:
                return ErrorCatalog.StepUpDenied;
            case UnsupportedFeatureException:
                return ErrorCatalog.FeatureNeedsNewerClient;
            case RemoteException remote:
                if (ErrorCatalog.Find(remote.Payload[ErrorCatalog.PayloadKey]?.GetValue<string>()) is { } sent) return sent;
                return remote.Payload["code"]?.GetValue<string>() switch
                {
                    "forbidden" => ErrorCatalog.NotAllowedForRole,
                    "throttled" => ErrorCatalog.Throttled,
                    "key_revoked" => ErrorCatalog.KeyRevoked,
                    "unsupported_command" => ErrorCatalog.FeatureNeedsNewerClient,
                    // Clients older than the ids say these in words only.
                    _ when message.StartsWith("Refused (", StringComparison.Ordinal) && message.Contains("the host and this client are the same computer", StringComparison.Ordinal) => ErrorCatalog.SameComputer,
                    _ when message.StartsWith("Unknown command", StringComparison.OrdinalIgnoreCase) => ErrorCatalog.FeatureNeedsNewerClient,
                    _ => ErrorCatalog.RefusedByComputer,
                };
            case AuthException:
                if (KeyRefusalPause.IsPauseMessage(message) || message.Contains("A later try was dropped without an answer", StringComparison.Ordinal))
                    return ErrorCatalog.IgnoringAfterRefusedKeys;
                if (message.StartsWith("Server failed to prove", StringComparison.Ordinal)) return ErrorCatalog.ServerDidNotProveKey;
                return ErrorCatalog.WrongKey;
            case AuthenticationException:
                if (message.Contains("TLS endpoint trust is missing", StringComparison.Ordinal)) return ErrorCatalog.CertificateNotApproved;
                if (message.Contains("RemoteCertificateValidationCallback", StringComparison.Ordinal)) return ErrorCatalog.CertificateChanged;
                return ErrorCatalog.TlsFailed;
            case SocketException socket:
                return socket.SocketErrorCode == SocketError.ConnectionRefused ? ErrorCatalog.ConnectionRefused : ErrorCatalog.Unreachable;
            case TimeoutException when message.StartsWith("Connection timed out", StringComparison.Ordinal):
                return ErrorCatalog.Unreachable;   // no connection was made in time, as opposed to a computer that stopped answering
            case TimeoutException:
            case OperationCanceledException:
                return ErrorCatalog.StoppedAnswering;
            case IOException io:
                return io.InnerException is SocketException inner ? Classify(inner) : ErrorCatalog.ConnectionDropped;
            case InvalidOperationException:
                if (message.StartsWith("No host key is configured", StringComparison.Ordinal)) return ErrorCatalog.NoKeyConfigured;
                if (message.EndsWith("Other actions are paused to avoid interrupting it.", StringComparison.Ordinal)) return ErrorCatalog.OtherOperationRunning;
                break;
        }
        return ex.InnerException is { } cause ? Classify(cause) : ErrorCatalog.Unexpected;
    }

    /// <summary>The message as shown: the exception's message with its error id at the end.</summary>
    public static string Describe(Exception ex) => ErrorIds.WithId(ex.Message, Classify(ex));

    /// <summary>True when a shown message ends with a connection error code (category 01): the computer is off, asleep or not answering.</summary>
    public static bool IsUnreachable(string shown) => ErrorIds.TrySplit(shown, out _, out var id) && id.StartsWith("01", StringComparison.Ordinal);

    /// <summary>
    /// The same exception with the id in its message, of the same type so callers that catch by type are unchanged. Types whose message
    /// cannot be replaced (a socket error) come back as they are; <see cref="Describe"/> adds their id where they are shown.
    /// </summary>
    public static Exception WithId(Exception ex)
    {
        var error = Classify(ex);
        if (ErrorIds.TrySplit(ex.Message, out _, out _)) return ex;
        var message = ErrorIds.WithId(ex.Message, error);
        return ex switch
        {
            UnsupportedFeatureException => ex,   // its message is made in its constructor, with the id (see ClientSession)
            StepUpDeniedException => new StepUpDeniedException(message),
            RemoteException remote => new RemoteException(message, remote.Payload),
            AuthException => new AuthException(message),
            AuthenticationException => new AuthenticationException(message, ex),
            TimeoutException => new TimeoutException(message, ex),
            IOException => new IOException(message, ex),
            InvalidOperationException when error != ErrorCatalog.Unexpected => new InvalidOperationException(message, ex),
            _ => ex,
        };
    }
}
