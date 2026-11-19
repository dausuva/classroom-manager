// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// What a status check of one computer says when it fails or finds something to point out, decided without a window (T-253 phase 2): the level and words for each kind of
/// failure, the key name shown beside the computer, and what a computer that missed a key change is told. The check itself, with its session and its "is this still the same
/// computer" guards, stays in the window.
/// </summary>
public static class StatusOutcomes
{
    /// <summary>
    /// A failure that needs no further question: the wrong key, or no key at all on this Host. Null for any other failure; ask whether the computer answers a ping and call
    /// <see cref="AfterPing"/>.
    /// </summary>
    public static (string Level, string Status)? Immediate(Exception exception, bool hostHasKeys) => exception switch
    {
        AuthException => ("bad", "Wrong shared key"),
        InvalidOperationException when !hostHasKeys => ("bad", exception.Message),
        _ => null,
    };

    /// <summary>
    /// The words for a failure that is not the key's: a computer that answers a ping is on and has a problem with the client or the connection; one that does not is offline.
    /// </summary>
    public static (string Level, string Status) AfterPing(Exception exception, bool computerAnswersPing)
    {
        if (computerAnswersPing && exception is AuthenticationException or IOException)
            return ("warn", "PC is on and the client answered, but the connection failed - check that Settings > Use TLS matches the client's setting (" + exception.Message + ")");
        if (computerAnswersPing)
            return ("warn", "PC is on, but the client is not reachable (service stopped, not installed, or blocked by firewall)");
        return ("", exception is TimeoutException || exception is SocketException ? "Offline (no reply)" : "Offline: " + exception.Message);
    }

    /// <summary>A saved address that another computer now answers from (DHCP gave it away): the status text, and the log line (without the computer name in front).</summary>
    public static string AddressTaken(string address, string heldMac) =>
        $"Address {address} now belongs to another computer (card {heldMac}); looking for this one";

    /// <summary>The key name shown beside a computer: the name of the key that worked, marked when it is the old one or a Teacher key.</summary>
    public static string KeyLabel(string keyName, bool usesPreviousKey, string role) =>
        keyName + (usesPreviousKey ? " (old key)" : "") + (role == nameof(OperatorRole.Teacher) ? " (Teacher)" : "");

    /// <summary>What a computer that still answers to the old key is told when the Host does not (or could not) finish the key change for it.</summary>
    public static (string Level, string Status) OldKey(bool autoFinish, string? finishFailure) => autoFinish
        ? ("warn", "Online - still on the old shared key: " + finishFailure)
        : ("warn", "Online - still on the old shared key (use Rotate key)");
}
