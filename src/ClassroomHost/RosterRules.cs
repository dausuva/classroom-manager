// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Net;
using System.Net.Sockets;

namespace ClassroomManager.Host;

/// <summary>
/// The decisions of the computer list, taken without a window (T-253 phase 2): which computers belong to the selected network and room, the list of rooms, what moving
/// computers into a room does to them, the one-line summary of the room, and the words describing the network scope. The list itself, its timers and its saving stay in the window.
/// </summary>
public static class RosterRules
{
    /// <summary>
    /// A computer belongs to the selected network when its address is inside it. A manually entered host name stays visible: the stored name alone does not reliably
    /// identify which address Windows will resolve at connection time. With no network selected every computer is shown.
    /// </summary>
    public static bool InNetwork(string address, Discovery.NetworkSubnet? scope) =>
        scope is null || !IPAddress.TryParse(address, out var parsed) || scope.Contains(parsed);

    public static bool InRoom(string computerRoom, string selectedRoom) =>
        selectedRoom.Length == 0 || computerRoom.Equals(selectedRoom, StringComparison.OrdinalIgnoreCase);

    /// <summary>The rooms in use among these computers, each once whatever its case, in alphabetical order.</summary>
    public static List<string> Rooms(IEnumerable<string> roomsOfComputers) =>
        roomsOfComputers.Where(room => room.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(room => room, StringComparer.OrdinalIgnoreCase).ToList();

    public const string AllRooms = "(all rooms)";

    /// <summary>What the room box lists, which entry is chosen, and the room that stays selected (a room nobody is in any more falls back to all rooms).</summary>
    public static (List<string> Items, int SelectedIndex, string Room) RoomChoices(IReadOnlyList<string> rooms, string selected)
    {
        var items = new List<string> { AllRooms };
        items.AddRange(rooms);
        if (selected.Length > 0 && !items.Contains(selected, StringComparer.OrdinalIgnoreCase)) selected = "";
        var index = selected.Length == 0 ? 0 : items.IndexOf(selected);
        return (items, index < 0 ? 0 : index, selected);
    }

    /// <summary>
    /// Puts the computers into a room, or out of every room when it is empty: out of a room a computer is only discovered (not watched), so its old answers are cleared;
    /// into a room it is checked again.
    /// </summary>
    public static void AssignRoom(IEnumerable<ClientInfo> computers, string room)
    {
        foreach (var computer in computers)
        {
            computer.Room = room;
            if (room.Length == 0)
            {
                computer.Level = "";
                computer.Status = "Discovered - assign to a room to monitor";
                computer.Ping = computer.Clock = computer.Uptime = computer.LastSeen = "";
                computer.ClockOffsetSec = null;
            }
            else computer.Status = "Checking...";
        }
    }

    /// <summary>The line above the list: how many are online, need attention, have the wrong key or are offline, and what is not watched.</summary>
    /// <param name="inNetwork">Every computer of the selected network.</param>
    /// <param name="teacherOnlyKey">Every computer that reports a role says Teacher: no Admin key is set.</param>
    public static string Summary(IReadOnlyList<ClientInfo> inNetwork, bool teacherOnlyKey, DateTime now)
    {
        var managed = inNetwork.Where(c => c.Room.Length > 0).ToList();
        int unassigned = inNetwork.Count - managed.Count;
        int ok = managed.Count(c => c.Level == "ok");
        int idle = managed.Count(c => c.Level == "idle");
        int warn = managed.Count(c => c.Level == "warn");
        int bad = managed.Count(c => c.Level == "bad");
        int off = managed.Count - ok - idle - warn - bad;
        int drift = managed.Count(c => c.ClockOffsetSec is double d && Math.Abs(d) > 5);

        var parts = new List<string> { $"{ok + idle} online" };
        if (idle > 0) parts.Add($"{idle} with nobody signed in");
        if (warn > 0) parts.Add($"{warn} need attention");
        if (bad > 0) parts.Add($"{bad} wrong key");
        parts.Add($"{off} offline");
        if (unassigned > 0) parts.Add($"{unassigned} discovered, not monitored (no room)");
        if (drift > 0) parts.Add($"{drift} with clock off by more than 5 s");
        if (teacherOnlyKey)
            parts.Add("Teacher key only: no Admin key is set, the computers refuse administrator actions");
        return $"{string.Join("  |  ", parts)}  (of {inNetwork.Count} on selected network, updated {now:HH:mm:ss})";
    }

    /// <summary>The words about the network the list and discovery are limited to, and the explanation shown on hover.</summary>
    public static (string Text, string ToolTip) NetworkScope(Discovery.SubnetSelection selection, string configuredSubnet)
    {
        if (selection.Subnet is null)
            return ("Network: no connected IPv4 subnet; showing all saved computers", "Connect an IPv4 network or choose another subnet in Host settings.");
        if (selection.ConfiguredSubnetUnavailable)
            return ($"Network: {selection.Subnet.Key} (fallback)",
                $"Saved subnet {configuredSubnet} is unavailable. The list and discovery currently use {selection.Subnet.DisplayName}.");
        var mode = configuredSubnet.Length == 0 ? "automatic LAN" : selection.Subnet.InterfaceName;
        return ($"Network: {selection.Subnet.Key} ({mode})",
            $"The computer list and discovery are limited to {selection.Subnet.DisplayName}. Change this in Host settings. Manually entered host names remain visible.");
    }

    /// <summary>Why a new Windows name for a computer is not accepted, or null: the name rules, the name it already has, and the names of the others in the list.</summary>
    public static string? RenameProblem(string name, string currentName, IEnumerable<string> namesOfOthers)
    {
        var problem = ClassroomManager.Shared.ComputerNameRules.Problem(name);
        if (problem is null && ClassroomManager.Shared.ComputerNameRules.Same(name, currentName)) problem = "The computer already has this name.";
        if (problem is null && namesOfOthers.Any(other => ClassroomManager.Shared.ComputerNameRules.Same(other, name))) problem = "Another computer in this list already has this name.";
        return problem;
    }
}
