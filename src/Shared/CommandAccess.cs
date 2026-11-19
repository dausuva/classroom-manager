// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>What the holder of an access key may do on a client. Higher values include lower ones.</summary>
public enum OperatorRole
{
    /// <summary>Looks only: the status shown in the computer list and the block lists. Changes nothing (T-190).</summary>
    Observer = 0,
    /// <summary>Classroom actions that do not change how the computer is set up.</summary>
    Teacher = 1,
    /// <summary>Everything, including software, system settings, accounts and the keys themselves.</summary>
    Admin = 2,
}

/// <summary>
/// The single place that says which role a command needs. The client enforces it for every request;
/// the Host only uses it to explain a refusal. Anything not listed for Teacher needs Admin, so a new
/// command is Admin-only until someone decides otherwise.
/// </summary>
public static class CommandAccess
{
    // What a key that may only look can ask for: every one of these only reads.
    static readonly HashSet<string> ObserverCommands = new(StringComparer.Ordinal)
    {
        Cmd.Ping, Cmd.ProcWindows, Cmd.BrowserSites, Cmd.WindowsPreferencesGet, Cmd.BlockHostsList, Cmd.BlockAppsList,
        Cmd.AppearanceCatalog, Cmd.AppearanceGet,
    };

    static readonly HashSet<string> TeacherCommands = new(StringComparer.Ordinal)
    {
        // Status shown in the computer list.
        Cmd.Ping, Cmd.ProcWindows, Cmd.BrowserSites, Cmd.WindowsPreferencesGet, Cmd.AppearanceCatalog, Cmd.AppearanceGet,
        // Attention and communication.
        Cmd.LockSet, Cmd.LockClear, Cmd.MessageShow, Cmd.MessageClear, Cmd.DisplayWake,
        // Lesson material. FsWrite, FsDelete and the staging commands are further limited on the
        // client to the staging folder the key itself created.
        Cmd.WebsiteOpen, Cmd.ProgramOpen,
        Cmd.FsWrite, Cmd.FsRead, Cmd.FsDeliver, Cmd.FsFind, Cmd.FsRecent, Cmd.FsDeliveredDelete,
        Cmd.FsDelete, Cmd.StagingList, Cmd.StagingDelete,
        // Desktop background of the signed-in student.
        Cmd.WallpaperSet, Cmd.WallpaperReset, Cmd.WallpaperDefault, Cmd.WallpaperColor,
        Cmd.WallpaperRecentList, Cmd.WallpaperRecentRemove,
        // The temporary exam overlay, including its device controls.
        Cmd.ExamModeSet, Cmd.ExamModeClear, Cmd.ExamDevicesSet,
        // The website and program block lists of the computers the key reaches, whoever added an entry.
        Cmd.BlockHostsList, Cmd.BlockHostsAdd, Cmd.BlockHostsSet, Cmd.BlockHostsClear, Cmd.BlockHostsReset,
        Cmd.BlockAppsList, Cmd.BlockAppsSet, Cmd.BlockAppsClear,
        // End of lesson.
        Cmd.PowerAction,
    };

    public static OperatorRole RequiredRole(string command) =>
        ObserverCommands.Contains(command) ? OperatorRole.Observer :
        TeacherCommands.Contains(command) ? OperatorRole.Teacher : OperatorRole.Admin;

    public static bool IsAllowed(OperatorRole role, string command) => role >= RequiredRole(command);

    /// <summary>Reads a stored role name; anything unrecognised is the least-privileged role.</summary>
    public static OperatorRole ParseRole(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "admin" => OperatorRole.Admin,
        "teacher" => OperatorRole.Teacher,
        _ => OperatorRole.Observer,
    };

    public static string Refusal(string command, OperatorRole role, string keyName) =>
        $"Not allowed with this key: '{command}' needs an {RequiredRole(command)} key, and '{keyName}' is a {role} key.";
}

/// <summary>Rules for the name an administrator gives an access key; the name appears in client logs.</summary>
public static class AccessKeyNames
{
    public const int MaxLength = 40;

    /// <summary>The built-in name of the lab's original key, which always has the Admin role.</summary>
    public const string Primary = "Lab key";

    public static bool IsAcceptable(string? name, out string reason)
    {
        var value = name ?? "";
        if (value.Trim().Length == 0) { reason = "The key name is empty."; return false; }
        if (value != value.Trim()) { reason = "The key name must not start or end with spaces."; return false; }
        if (value.Length > MaxLength) { reason = $"The key name must be at most {MaxLength} characters."; return false; }
        if (value.Any(char.IsControl)) { reason = "The key name must not contain control characters."; return false; }
        if (value.Equals(Primary, StringComparison.OrdinalIgnoreCase)) { reason = $"'{Primary}' is reserved for the lab's original key."; return false; }
        reason = "";
        return true;
    }
}
