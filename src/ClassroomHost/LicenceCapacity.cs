// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>What <see cref="LicenceCapacity.Plan"/> decided for one action: the computers it goes to and the online ones that must wait for a licence.</summary>
public sealed record CapacityPlan(IReadOnlyList<ClientInfo> Acting, IReadOnlyList<ClientInfo> Waiting);

/// <summary>
/// The free tier of the official Host (T-220): without a licence it works with up to <see cref="FreeComputers"/> computers online at the same time;
/// a licence covers the number of computers it names. A build made from the source has no limit. The limit is never a reason to undo anything:
/// computers that wait are simply not sent new actions, and a school that has held a licence keeps its capacity when the licence ends.
/// </summary>
public static class LicenceCapacity
{
    public const int FreeComputers = 3;

    /// <summary>Command-line commands the limit never applies to: reading status, waking, and the maintenance that keeps computers safe (key changes, Client updates).</summary>
    public static readonly string[] CommandsNotLimited = ["status", "wake", "derive-keys", "update"];

    /// <summary>The limit that applies to a command line or scheduled run on this Host now; null when there is none.</summary>
    public static int? AllowedNow() =>
        Allowed(BuildEdition.IsOfficial, LicenceKeys.Trusted.Length > 0, HostLicence.Read(DateOnly.FromDateTime(DateTime.Now)));

    /// <summary>True when something answers on the computer's Client port within the time given; this is how a command-line run learns who is online.</summary>
    public static async Task<bool> IsOnlineAsync(string address, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try { await tcp.ConnectAsync(address, port > 0 ? port : Protocol.DefaultTcpPort, linked.Token); return true; }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or IOException) { return false; }
    }

    /// <summary>How many computers may be online and managed at once; null when there is no limit (source builds, custom agreements, or a licence this build cannot judge).</summary>
    public static int? Allowed(bool officialBuild, bool buildHasKeys, LicenceCheck licence)
    {
        if (!officialBuild || !buildHasKeys) return null;
        if (licence.KeyUnknown) return null;
        if (licence.Signed && licence.Terms is { } terms) return terms.Custom ? null : Math.Max(terms.Computers, FreeComputers);
        return FreeComputers;
    }

    /// <summary>
    /// The first <paramref name="allowed"/> computers of the roster that are online are the ones in use. A target that is offline goes along (its action fails on its own);
    /// an online target outside the computers in use waits.
    /// </summary>
    public static CapacityPlan Plan(IEnumerable<ClientInfo> roster, IReadOnlyList<ClientInfo> targets, int allowed)
    {
        var inUse = roster.Where(ComputerSelection.IsOnline).Take(Math.Max(0, allowed)).ToHashSet();
        var acting = targets.Where(target => !ComputerSelection.IsOnline(target) || inUse.Contains(target)).ToList();
        var waiting = targets.Where(target => ComputerSelection.IsOnline(target) && !inUse.Contains(target)).ToList();
        return new CapacityPlan(acting, waiting);
    }

    /// <summary>The message shown when some selected computers wait: what went where, why, and what to do about it.</summary>
    public static string Explain(int allowed, CapacityPlan plan)
    {
        var waiting = string.Join(", ", plan.Waiting.Select(c => c.Name));
        if (plan.Acting.Count == 0)
            return $"Nothing was sent. Classroom Manager is free for up to {allowed} computers online at the same time, and {allowed} other computers are already in use.\n\n" +
                   $"Waiting for a licence: {waiting}.\n\nTo manage more computers at once, add your licence file in Help > About.";
        var acting = string.Join(", ", plan.Acting.Select(c => c.Name));
        return $"Classroom Manager is free for up to {allowed} computers online at the same time.\n\n" +
               $"Sent to: {acting}.\nWaiting for a licence: {waiting}.\n\n" +
               "To manage more computers at once, add your licence file in Help > About. Nothing already running on any computer was changed.";
    }
}
