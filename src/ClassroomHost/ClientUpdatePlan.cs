// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// What the Update client dialog decides (T-253 phase 2): which computers each choice means, the batch size, which of them can take the chosen build, the hash and the
/// arguments sent. The dialog, its message boxes and the log texts stay in the window so they keep their translations.
/// </summary>
public static class ClientUpdatePlan
{
    /// <summary>What happens to the list of targets after one check of the chosen build.</summary>
    public enum Fit { All, Some, None }

    /// <summary>The verdict of one check: the text to show when not all fit, and the computers that do.</summary>
    public sealed record Screening(Fit Fit, string Explanation, List<ClientInfo> Fitting);

    public static bool InProgress(IEnumerable<ClientInfo> clients) => clients.Any(client => client.ExclusiveOperation.Length > 0);

    public static List<ClientInfo> Online(IEnumerable<ClientInfo> inView) => inView.Where(client => client.Status.StartsWith("Online")).ToList();

    public static string[] TargetChoices(int picked, int inView, int online, string room)
    {
        var roomText = room.Length == 0 ? "all rooms" : $"room {room}";
        return
        [
            $"Selected computers ({picked})",
            $"Every computer in the list - {roomText} ({inView})",
            $"Only online computers - {roomText} ({online})",
        ];
    }

    /// <summary>The selection when there is one, otherwise the online computers.</summary>
    public static int DefaultChoice(int picked) => picked > 0 ? 0 : 2;

    public static List<ClientInfo> Targets(int choice, List<ClientInfo> picked, List<ClientInfo> inView, List<ClientInfo> onlineInView) =>
        choice switch { 1 => inView, 2 => onlineInView, _ => picked };

    /// <summary>Blank, zero, negative or not a number all mean "all at once" (0).</summary>
    public static int Batch(string typed) => int.TryParse(typed, out var batch) && batch > 0 ? batch : 0;

    /// <summary>
    /// A build without .NET inside must only go to computers known to have the runtime: a Client that cannot start after replacing itself is unreachable,
    /// and older Clients do not check before doing so. A build that carries its own runtime fits everywhere.
    /// </summary>
    public static Screening ScreenRuntime(List<ClientInfo> targets, ClientBuildRuntime build)
    {
        if (!build.UsesSharedRuntime) return new(Fit.All, "", targets);
        var readiness = targets.ToLookup(client => ClientUpdateRuntime.Classify(client.DesktopRuntime, build.MajorVersion));
        var ready = readiness[RuntimeReadiness.Ready].ToList();
        if (ready.Count == targets.Count) return new(Fit.All, "", targets);
        var explanation = ClientUpdateRuntime.Explain(build,
            readiness[RuntimeReadiness.Missing].Select(client => client.Name).ToList(),
            readiness[RuntimeReadiness.Unknown].Select(client => client.Name).ToList(), ready.Count);
        return new(ready.Count == 0 ? Fit.None : Fit.Some, explanation, ready);
    }

    /// <summary>A build for 32-bit Windows must not go to a 64-bit Client or the other way round; each Client refuses it anyway.</summary>
    public static Screening ScreenArchitecture(List<ClientInfo> targets, string buildArchitecture)
    {
        var wrong = targets.Where(client => ClientUpdateArchitecture.Mismatch(buildArchitecture, client.Architecture)).ToList();
        if (wrong.Count == 0) return new(Fit.All, "", targets);
        var explanation = ClientUpdateArchitecture.Explain(buildArchitecture, wrong.Select(client => client.Name).ToList(), targets.Count - wrong.Count);
        return new(wrong.Count == targets.Count ? Fit.None : Fit.Some, explanation, targets.Except(wrong).ToList());
    }

    public static string Sha256Of(string file)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public static string Title(string file) => $"Update client ({Path.GetFileName(file)})";

    public static JsonObject Arguments(string remotePath, string sha256, bool force) =>
        new() { ["path"] = remotePath, ["sha256"] = sha256, ["force"] = force };
}
