// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// Which computers this Host's Exam mode covers. Exam mode is started for the computers shown at that
/// moment (one room, usually), so the list has to be remembered: the status refresh covers every room,
/// and turning Exam mode off must reach the same computers even when another room is on screen.
/// A computer is listed under its client id and its address, because the id is only known once the
/// computer has answered and the address can change afterwards.
/// </summary>
public static class ExamModeScope
{
    public static bool Contains(IReadOnlyCollection<string> keys, string clientId, string address) =>
        Keys(clientId, address).Any(key => keys.Contains(key, StringComparer.OrdinalIgnoreCase));

    public static void Add(List<string> keys, string clientId, string address)
    {
        foreach (var key in Keys(clientId, address))
            if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase)) keys.Add(key);
    }

    public static void Remove(List<string> keys, string clientId, string address)
    {
        foreach (var key in Keys(clientId, address))
            keys.RemoveAll(existing => existing.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Records what the command line did (ClassroomCli exam on|off): the computers it succeeded on
    /// join or leave the list, and Exam mode counts as on while any computer is listed.
    /// </summary>
    public static void Record(HostSettings settings, bool on, IEnumerable<(string ClientId, string Address)> succeeded)
    {
        foreach (var (clientId, address) in succeeded)
        {
            if (on) Add(settings.ExamModeComputers, clientId, address);
            else
            {
                Remove(settings.ExamModeComputers, clientId, address);
                Remove(settings.ExamModePendingClear, clientId, address);
            }
        }
        settings.ExamModeEnabled = settings.ExamModeComputers.Count > 0;
    }

    /// <summary>
    /// Takes over the exam list another program saved (the command line, while the Host window is
    /// open). Returns true when it differed from what this program had.
    /// </summary>
    public static bool Adopt(HostSettings mine, HostSettings saved)
    {
        bool same = mine.ExamModeEnabled == saved.ExamModeEnabled &&
                    mine.ExamModeComputers.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(saved.ExamModeComputers);
        if (same) return false;
        mine.ExamModeEnabled = saved.ExamModeEnabled;
        mine.ExamModeComputers = saved.ExamModeComputers.ToList();
        return true;
    }

    static IEnumerable<string> Keys(string clientId, string address)
    {
        if (!string.IsNullOrWhiteSpace(clientId)) yield return "id:" + clientId.Trim();
        if (!string.IsNullOrWhiteSpace(address)) yield return "address:" + address.Trim();
    }
}
