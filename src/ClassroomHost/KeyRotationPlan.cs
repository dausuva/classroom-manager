// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// What the Rotate shared key dialog decides (T-253 phase 2): the choices of computers, which one is offered first, whether the new key may be used, the computers
/// that are on another lab key, and the closing summary. The dialogs and the log texts stay in the window so they keep their translations; the key change itself is
/// <see cref="KeyChangePlan"/> and <c>KeyRotation</c>.
/// </summary>
public static class KeyRotationPlan
{
    public sealed record Choice(string Label, List<ClientInfo> Targets);

    /// <summary>Every saved computer on the selected network; the room, when one is open; the selection.</summary>
    public static List<Choice> Choices(List<ClientInfo> onNetwork, string room, List<ClientInfo> inView, List<ClientInfo> picked)
    {
        var choices = new List<Choice> { new($"Every saved computer on the selected network ({onNetwork.Count})", onNetwork) };
        if (room.Length > 0) choices.Add(new($"Every computer in room {room} ({inView.Count})", inView));
        choices.Add(new($"Selected computers ({picked.Count})", picked));
        return choices;
    }

    /// <summary>The selection when there is one and no room is open, otherwise every saved computer.</summary>
    public static int DefaultChoice(int picked, string room, int choiceCount) => picked > 0 && room.Length == 0 ? choiceCount - 1 : 0;

    /// <summary>The choice whose label was picked; the first when the label is not found.</summary>
    public static Choice Pick(IReadOnlyList<Choice> choices, string label)
    {
        var index = 0;
        for (var i = 0; i < choices.Count; i++)
            if (choices[i].Label == label) { index = i; break; }
        return choices[index];
    }

    /// <summary>Why the new key cannot be used, or null when it can.</summary>
    public static string? Refusal(string newKey, string currentKey)
    {
        if (!SharedKey.IsAcceptable(newKey, out var reason)) return reason;
        return newKey == currentKey ? "That is already the current key." : null;
    }

    /// <summary>Blank, zero, negative or not a number all mean "all at once" (0).</summary>
    public static int Batch(string typed) => ClientUpdatePlan.Batch(typed);

    /// <summary>The computers that are not on the default lab key, with the key each one uses; ten at most.</summary>
    public static string IncompatibleText(IReadOnlyList<ClientInfo> incompatible) =>
        "Rotate shared key changes only the default lab key. Refresh status and select only computers using that key.\n\n" +
        string.Join("\n", incompatible.Take(10).Select(client =>
            $"{client.Name} — {(client.KeyName.Length > 0 ? client.KeyName : "key not identified")}")) +
        (incompatible.Count > 10 ? "\n..." : "");

    /// <summary>The closing line: how many computers are on the new key and what happens to the others.</summary>
    public static string Summary(int changed, int total, int pending, bool autoFinish)
    {
        var summary = $"{changed} of {total} computer(s) now use the new key.";
        if (pending > 0)
            summary += $" {pending} computer(s) in the list are not confirmed on it yet" + (autoFinish
                ? "; they are switched automatically the next time this app sees them."
                : "; run Rotate key again for them.");
        return summary;
    }
}
