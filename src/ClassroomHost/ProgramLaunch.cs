// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>What the Programs windows ask of the main window: open a program, or open the install dialog, on given computers.</summary>
public sealed record ProgramWindowActions(
    Func<IReadOnlyList<ClientInfo>, string, Task> OpenProgram,
    Action<IReadOnlyList<ClientInfo>> Install);

/// <summary>Finds the program an installed-program entry starts, from what Windows records about it.</summary>
public static class ProgramLaunch
{
    /// <summary>Executables that belong to setting a program up or removing it, never to using it.</summary>
    static readonly string[] NotTheProgram = ["unins", "uninst", "setup", "install", "msiexec", "update", "remove"];

    /// <summary>
    /// The program's own executable when the entry's icon points at one, otherwise "". Install folders
    /// alone do not say which file to start, so they give nothing here.
    /// </summary>
    public static string Executable(ProgramEntry entry)
    {
        var path = DisplayIconPath(entry.DisplayIcon);
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "";
        var file = System.IO.Path.GetFileNameWithoutExtension(path);
        return NotTheProgram.Any(word => file.Contains(word, StringComparison.OrdinalIgnoreCase)) ? "" : path;
    }

    /// <summary>What the Open program dialog starts with: the executable, or else the install folder so only the file name is left to type.</summary>
    public static string Suggestion(IEnumerable<ProgramEntry> entries)
    {
        var list = entries.ToList();
        var program = list.Select(Executable).FirstOrDefault(path => path.Length > 0);
        if (program is not null) return program;
        var folder = list.Select(entry => entry.InstallLocation.Trim()).FirstOrDefault(path => path.Length > 0);
        return folder is null ? "" : folder.TrimEnd('\\') + "\\";
    }

    /// <summary>The name to search the installed programs for, from a process name: "firefox.exe" gives "firefox".</summary>
    public static string SearchName(string processName) =>
        System.IO.Path.GetFileNameWithoutExtension(processName.Trim());

    /// <summary>Asks for the program name to search for, starting from <paramref name="suggested"/>; "" when cancelled.</summary>
    public static string AskSearchName(System.Windows.Window owner, string suggested)
    {
        var dlg = new FormDialog("Find program to uninstall", owner)
            .Note("The installed programs whose name contains this text are listed, with Uninstall there.")
            .Text("name", "Program _name", suggested);
        return dlg.Ask("Find") ? dlg.S("name") : "";
    }

    /// <summary>The file part of a DisplayIcon value: quotes and a trailing ",0" icon index taken off.</summary>
    public static string DisplayIconPath(string value)
    {
        var text = value.Trim();
        if (text.StartsWith('"'))
        {
            var closingQuote = text.IndexOf('"', 1);
            return closingQuote > 1 ? text[1..closingQuote] : text.Trim('"');
        }
        var iconIndex = text.LastIndexOf(',');
        return iconIndex > 2 && int.TryParse(text[(iconIndex + 1)..], out _) ? text[..iconIndex].Trim() : text;
    }
}
