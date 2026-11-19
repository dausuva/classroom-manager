// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// The decisions of the Install software dialogs, taken without a window (T-253 phase 2): which computers "Install on" means, what the operation is called, and, for an
/// installer already uploaded to the computers, what the teacher's choices come to (who it runs as, the timeout, the warning, the review).
/// </summary>
public static class SoftwareInstallPlan
{
    public const string Elevated = "Elevated (machine-wide, SYSTEM)";
    public const string StandardUser = "Standard user (signed-in student)";
    public const int DefaultTimeoutSeconds = 1800, MinTimeoutSeconds = 30, MaxTimeoutSeconds = 86400;

    public static string RoomText(string room) => room.Length == 0 ? "all rooms" : $"room {room}";

    /// <summary>The three answers to "Install on": the highlighted computers, everyone in the list, and only those online.</summary>
    public static string[] TargetChoices(int picked, int inView, int onlineInView, string room)
    {
        var roomText = RoomText(room);
        return
        [
            $"Selected computers ({picked})",
            $"Every computer in the list - {roomText} ({inView})",
            $"Only online computers - {roomText} ({onlineInView})",
        ];
    }

    /// <summary>With some computers highlighted "Selected" is offered first, otherwise "Only online".</summary>
    public static int DefaultTargetIndex(int picked) => picked > 0 ? 0 : 2;

    public static List<ClientInfo> Targets(int choice, List<ClientInfo> picked, List<ClientInfo> inView, List<ClientInfo> onlineInView) =>
        choice switch { 1 => inView, 2 => onlineInView, _ => picked };

    public static List<ClientInfo> Online(IEnumerable<ClientInfo> inView) => inView.Where(client => client.Status.StartsWith("Online")).ToList();

    public const string NoComputers = "No computers to run on. Select some in the list, or choose another option under \"Install on\".";

    /// <summary>What the operation is called in Operations: the package, the registry file, or the installer's file name.</summary>
    public static string Title(SoftwareAction action, string packageId, string localFile, string remotePath) => action switch
    {
        SoftwareAction.WingetInstall => $"Install {packageId}",
        SoftwareAction.WingetUninstall => $"Uninstall {packageId}",
        SoftwareAction.Registry => $"Apply registry file {Path.GetFileName(localFile)}",
        _ => $"Install {(remotePath.Length > 0 ? Path.GetFileName(remotePath) : Path.GetFileName(localFile))}",
    };

    // ---- an installer already uploaded to the computers --------------------------------------

    /// <summary>The silent parameters that go with an installer by default: an MSI is run quietly, an EXE needs its own.</summary>
    public static string DefaultArguments(bool isMsi) => isMsi ? "/qn /norestart" : "";

    public static bool IsMsi(string relativePath) => relativePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the configure step of an uploaded installer comes to.</summary>
    public sealed record StagedChoices(string Level, string Arguments, int TimeoutSeconds, string TimeoutText, bool DeleteAfter, string Warning,
        IReadOnlyList<BulkOperationReviewItem> ReviewItems, string ReviewLabel);

    public static StagedChoices Staged(string relativePath, string levelChoice, string arguments, string timeoutText, bool deleteAfter)
    {
        var isMsi = IsMsi(relativePath);
        var args = arguments.Trim();
        var level = levelChoice.StartsWith("Standard", StringComparison.Ordinal) ? Levels.User : Levels.System;
        var timeout = int.TryParse(timeoutText, out var parsed) && parsed is >= MinTimeoutSeconds and <= MaxTimeoutSeconds ? parsed : DefaultTimeoutSeconds;
        var warning = !isMsi && args.Length == 0 && level == Levels.System
            ? "No silent parameters were entered. An EXE running as SYSTEM cannot show its wizard on the signed-in user's desktop and may wait until the timeout. Check the installer's documentation for silent switches."
            : "";
        IReadOnlyList<BulkOperationReviewItem> review =
        [
            new("Installer", relativePath),
            new("Run as", levelChoice),
            new("Parameters", args.Length == 0 ? "(none)" : args),
            new("Timeout", $"{timeout} seconds"),
            new("After success", deleteAfter ? "Delete the staged installer" : "Keep the staged installer"),
        ];
        return new StagedChoices(level, args, timeout, timeout.ToString(), deleteAfter, warning, review, warning.Length > 0 ? "Install anyway" : "Install");
    }
}
