// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Globalization;
using System.IO;

namespace ClassroomManager.Host;

/// <summary>The kinds of action a custom quick action can run, each with its own saved settings.</summary>
public static class CustomQuickActionKinds
{
    public const string OpenProgram = "open-program";
    public const string OpenWebsite = "open-website";
    public const string Message = "message";
    public const string Lock = "lock";
    public const string Power = "power";
    public const string SyncFiles = "sync-files";

    /// <summary>Kind and the name the editor shows for it, in the order offered.</summary>
    public static readonly IReadOnlyList<(string Kind, string Label)> All =
    [
        (OpenProgram, "Open a program"),
        (OpenWebsite, "Open a website"),
        (Message, "Show a message"),
        (Lock, "Lock screens with a message"),
        (Power, "Sleep, shut down or restart after a delay"),
        (SyncFiles, "Sync files from the students to this computer"),
    ];

    public static string Label(string kind) => All.FirstOrDefault(entry => entry.Kind == kind).Label ?? kind;

    public static readonly string[] PowerActions = ["Sleep", "Shut down", "Restart"];
}

/// <summary>
/// A quick action the teacher made: one action with its settings saved, run on the current selection
/// from the Quick actions bar.
/// </summary>
public sealed class CustomQuickAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = CustomQuickActionKinds.Message;

    /// <summary>Program path (open a program), web address (open a website) or folder on the student PC (sync).</summary>
    public string Target { get; set; } = "";
    /// <summary>Program arguments, or the file name pattern of a sync.</summary>
    public string Arguments { get; set; } = "";
    /// <summary>Message text (message, lock, and the notice before shutdown or restart).</summary>
    public string Text { get; set; } = "";
    /// <summary>"Sleep", "Shut down" or "Restart".</summary>
    public string PowerAction { get; set; } = "Sleep";
    public int DelaySeconds { get; set; }
    /// <summary>Folder on this computer a sync copies into, one subfolder per student computer.</summary>
    public string LocalFolder { get; set; } = "";
    public bool IncludeSubfolders { get; set; } = true;
    /// <summary>A sync copies a changed file over the teacher's earlier copy; off, it only adds files that are missing.</summary>
    public bool Overwrite { get; set; } = true;

    /// <summary>What is wrong with the saved settings, or "" when the action can run.</summary>
    public string Problem()
    {
        if (Name.Trim().Length == 0) return "Give the quick action a name.";
        switch (Kind)
        {
            case CustomQuickActionKinds.OpenProgram:
                return Target.Trim().Length == 0 ? "Enter the program to open." : "";
            case CustomQuickActionKinds.OpenWebsite:
                return Uri.TryCreate(Target.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                    ? "" : "Enter a web address that starts with http:// or https://.";
            case CustomQuickActionKinds.Message:
            case CustomQuickActionKinds.Lock:
                return Text.Trim().Length == 0 ? "Enter the message." : "";
            case CustomQuickActionKinds.Power:
                if (!CustomQuickActionKinds.PowerActions.Contains(PowerAction)) return "Choose Sleep, Shut down or Restart.";
                return DelaySeconds is < 0 or > 3600 ? "The delay must be between 0 and 3600 seconds." : "";
            case CustomQuickActionKinds.SyncFiles:
                if (Target.Trim().Length == 0) return "Enter the folder on the student computers.";
                return Path.IsPathFullyQualified(LocalFolder.Trim()) ? "" : "Choose the folder on this computer to sync into.";
            default:
                return "This kind of quick action is not known to this version.";
        }
    }

    /// <summary>One line for the list of quick actions.</summary>
    public string Summary => Kind switch
    {
        CustomQuickActionKinds.OpenProgram => $"{CustomQuickActionKinds.Label(Kind)}: {Target} {Arguments}".TrimEnd(),
        CustomQuickActionKinds.OpenWebsite or CustomQuickActionKinds.Message or CustomQuickActionKinds.Lock => $"{CustomQuickActionKinds.Label(Kind)}: {(Kind == CustomQuickActionKinds.OpenWebsite ? Target : Text)}",
        CustomQuickActionKinds.Power => $"{PowerAction} in {DelaySeconds.ToString(CultureInfo.InvariantCulture)} s",
        CustomQuickActionKinds.SyncFiles => $"{CustomQuickActionKinds.Label(Kind)}: {Target} -> {LocalFolder}",
        _ => Kind,
    };

    public CustomQuickAction Copy() => (CustomQuickAction)MemberwiseClone();
}

/// <summary>The usual folders offered in the drop-downs of Collect files and of a sync quick action.</summary>
public static class CollectFolders
{
    /// <summary>Folders of the signed-in student; the client expands the tokens on the student computer.</summary>
    public static readonly IReadOnlyList<(string Value, string Name)> OnStudentComputer =
    [
        ("%DESKTOP%", "Desktop"),
        ("%DOCUMENTS%", "Documents"),
        ("%DOWNLOADS%", "Downloads"),
        ("%USERPROFILE%", "The student's user folder"),
    ];

    /// <summary>The folders used before, newest first, then a folder in this user's Documents and the Desktop.</summary>
    public static IReadOnlyList<(string Value, string Name)> OnThisComputer(IEnumerable<string> recent)
    {
        var documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Collected files");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var choices = recent.Select(folder => (folder, "")).ToList();
        foreach (var (folder, name) in new[] { (documents, "Documents"), (desktop, "Desktop") })
            if (folder.Length > Path.GetPathRoot(folder)?.Length && !choices.Any(choice => choice.folder.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                choices.Add((folder, name));
        return choices;
    }
}

/// <summary>A file found on a student computer: its path under the synced folder, size and last change as the computer reports it.</summary>
public sealed record RemoteFile(string RelativePath, long Size, DateTime Modified);

/// <summary>What a local copy looks like now, or null when there is none.</summary>
public sealed record LocalFile(long Size, DateTime Modified);

/// <summary>
/// One-way sync like rsync without --delete: a file is copied when the teacher's copy is missing, differs in
/// size, or has another change time (to the second). Nothing is deleted on either side. Without
/// <paramref name="overwrite"/> (like rsync --ignore-existing) only missing files are copied.
/// </summary>
public static class FolderSync
{
    public static List<RemoteFile> ToCopy(IEnumerable<RemoteFile> remote, Func<string, LocalFile?> local, bool overwrite = true) =>
        remote.Where(file => SafeRelativePath(file.RelativePath))
            .Where(file => local(file.RelativePath) is not { } copy
                || overwrite && (copy.Size != file.Size || Math.Abs((copy.Modified - file.Modified).TotalSeconds) >= 1))
            .ToList();

    /// <summary>A path that stays inside the folder it is joined to.</summary>
    public static bool SafeRelativePath(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) && !path.Split('\\', '/').Any(part => part == "..");

    /// <summary>The "modified" value the client sends for a found file ("2026-10-14T09:15:02", the computer's local time).</summary>
    public static DateTime ParseModified(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : DateTime.MinValue;
}
