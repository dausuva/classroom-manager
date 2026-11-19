// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace ClassroomManager.Host;

/// <summary>
/// What the Uninstall client dialog asks and checks, and how the Host knows the client is really gone (T-253 phase 2): which computers it may be run on, the typed
/// name, the arguments, what the computer answered, and the wait until it stops answering. The dialog's labels and the log lines stay in the window so they keep their translations.
/// </summary>
public static class ClientUninstallPlan
{
    public const string OnlyOne = "Select exactly one computer: the client is removed from one computer at a time.";

    /// <summary>How long the Host waits for the computer to stop answering, how often it asks, and how many silent answers in a row mean it is gone.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3), Interval = TimeSpan.FromSeconds(3);
    public const int SilentAnswersNeeded = 3;

    /// <summary>The removal is confirmed by typing the computer's name, in any case.</summary>
    public static bool NameMatches(string typed, string computerName) => typed.Equals(computerName, StringComparison.OrdinalIgnoreCase);

    public static string NameMismatch(string computerName) => $"The name does not match {computerName}, so nothing was removed.";

    /// <summary>The arguments of <c>client.uninstall</c>: keep the client's settings for a later reinstall, and also remove the lab code-signing trust.</summary>
    public static JsonObject Arguments(bool keepData, bool removeLabTrust) => new() { ["keepData"] = keepData, ["removeLabTrust"] = removeLabTrust };

    /// <summary>What the computer said when it started the removal: where it writes its log, and what became of the lab trust (null when it said nothing).</summary>
    public sealed record Started(string? LogPath, string? LabTrustFingerprint, string? LabTrustNote);

    public static Started ReadAnswer(JsonObject answer) => new(
        answer["log"]?.GetValue<string>(),
        answer["labTrust"]?.GetValue<string>() is { Length: > 0 } fingerprint ? fingerprint : null,
        answer["labTrustNote"]?.GetValue<string>() is { Length: > 0 } note ? note : null);

    /// <summary>
    /// Asks until the computer has stopped answering: <paramref name="silentAnswersNeeded"/> failures in a row, with an answer in between starting the count again.
    /// Returns false when it still answers at the end of <paramref name="timeout"/>. <paramref name="delay"/> and <paramref name="now"/> exist so a test needs no real waiting.
    /// </summary>
    public static async Task<bool> WaitUntilGoneAsync(Func<Task<bool>> answers, TimeSpan interval, TimeSpan timeout, int silentAnswersNeeded = SilentAnswersNeeded,
        Func<TimeSpan, Task>? delay = null, Func<DateTime>? now = null)
    {
        delay ??= Task.Delay;
        now ??= () => DateTime.UtcNow;
        var deadline = now() + timeout;
        var silent = 0;
        while (now() < deadline)
        {
            await delay(interval);
            silent = await answers() ? 0 : silent + 1;
            if (silent >= silentAnswersNeeded) return true;
        }
        return false;
    }

    /// <summary>True when something accepts a connection at the address and port within three seconds.</summary>
    public static async Task<bool> AnswersAsync(string address, int port)
    {
        using var tcp = new TcpClient();
        try { return await tcp.ConnectAsync(address, port).WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(task => task.IsCompletedSuccessfully); }
        catch (Exception) { return false; }
    }
}
