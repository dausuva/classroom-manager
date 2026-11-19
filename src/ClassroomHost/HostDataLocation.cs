// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;

namespace ClassroomManager.Host;

/// <summary>
/// Which folder holds the Host's per-user files (settings, keys, schedules, wallpapers, logs). By default it is the Windows user's own
/// <c>%AppData%\ClassroomManager</c>; three things move it, in this order:
/// <list type="number">
/// <item><c>--data-folder &lt;folder&gt;</c> on the command line (the Host, ClassroomCli and Classroom Health all take it);</item>
/// <item>the <c>CLASSROOM_DATA_FOLDER</c> environment variable;</item>
/// <item>a file named <c>ClassroomHost.portable</c> beside the program, which means "use <c>ClassroomHostData</c> beside the program".</item>
/// </list>
/// Everything in the Host that reads or writes those files goes through <see cref="HostSettings.DataFolder"/>, so this is the whole change.
/// The keys inside are still protected with Windows DPAPI for the current user: a folder moved to another PC or another Windows user shows the
/// settings but cannot decrypt the keys.
/// </summary>
public static class HostDataLocation
{
    public const string Switch = "--data-folder";
    public const string Variable = "CLASSROOM_DATA_FOLDER";
    public const string PortableMarker = "ClassroomHost.portable";
    public const string PortableFolder = "ClassroomHostData";

    /// <param name="Folder">The folder to use, or null for the default.</param>
    /// <param name="Source">Where the choice came from, for a message.</param>
    /// <param name="Args">The arguments without <c>--data-folder</c> and its value.</param>
    public sealed record Choice(string? Folder, string Source, string[] Args);

    /// <summary>Works out the folder from the arguments, the environment and the program's folder. It changes nothing.</summary>
    public static Choice Resolve(IReadOnlyList<string> args, string programFolder, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var rest = new List<string>();
        string? given = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals(Switch, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{Switch} needs a folder.");
                given = args[++i];
            }
            else if (arg.StartsWith(Switch + "=", StringComparison.OrdinalIgnoreCase)) given = arg[(Switch.Length + 1)..];
            else rest.Add(arg);
        }
        if (given is not null)
        {
            if (string.IsNullOrWhiteSpace(given)) throw new ArgumentException($"{Switch} needs a folder.");
            return new Choice(Path.GetFullPath(given.Trim().Trim('"')), Switch, [.. rest]);
        }
        if (environment(Variable) is { Length: > 0 } fromEnvironment && !string.IsNullOrWhiteSpace(fromEnvironment))
            return new Choice(Path.GetFullPath(fromEnvironment.Trim().Trim('"')), Variable, [.. rest]);
        if (File.Exists(Path.Combine(programFolder, PortableMarker)))
            return new Choice(Path.Combine(Path.GetFullPath(programFolder), PortableFolder), PortableMarker, [.. rest]);
        return new Choice(null, "", [.. rest]);
    }

    /// <summary>
    /// Points the Host at the chosen folder (creating it, and checking that a file can be written there) and returns the arguments without the switch.
    /// With no choice nothing changes. Throws <see cref="InvalidOperationException"/> with a reason when the folder cannot be used, before anything was read.
    /// </summary>
    public static string[] Apply(IReadOnlyList<string> args, string? programFolder = null, Func<string, string?>? environment = null, Action<string>? setFolder = null)
    {
        Choice choice;
        try { choice = Resolve(args, programFolder ?? AppContext.BaseDirectory, environment); }
        catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message, ex); }
        if (choice.Folder is null) return choice.Args;
        try
        {
            Directory.CreateDirectory(choice.Folder);
            var probe = Path.Combine(choice.Folder, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException($"The folder {choice.Folder} (from {choice.Source}) cannot be used: {ex.Message}", ex);
        }
        (setFolder ?? (folder => HostSettings.DataFolder = folder))(choice.Folder);
        return choice.Args;
    }
}
