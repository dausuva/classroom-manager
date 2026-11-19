// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ClassroomManager.Host;

/// <summary>Why the Host does or does not offer to install itself (Help > Install on this computer).</summary>
public enum SelfInstallState
{
    /// <summary>A single-file program running from somewhere that is not an install: the item is offered.</summary>
    Offered,
    /// <summary>Windows installs, updates and removes a packaged (Microsoft Store) program itself.</summary>
    Packaged,
    /// <summary>Already running from the per-user install folder or from Program Files (the Setup program).</summary>
    AlreadyInstalled,
    /// <summary>A ClassroomHost.portable file sits beside the program: the person chose to keep it portable.</summary>
    PortableMarker,
    /// <summary>winget put the program there and winget upgrades and removes it.</summary>
    ManagedByWinget,
    /// <summary>The build that needs the .NET runtime beside it is several files; only the single-file program copies itself.</summary>
    MoreThanOneFile,
}

/// <summary>Where the per-user install puts things. Tests give it scratch locations.</summary>
public sealed record SelfInstallPaths(string InstallFolder, string StartMenuFolder, string RegistryKeyPath)
{
    public static SelfInstallPaths ForThisUser() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Classroom Manager"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs)),
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DausuvaClassroomHostUser");

    public string ProgramPath => Path.Combine(InstallFolder, HostSelfInstall.ExeName);
    public string ShortcutPath => Path.Combine(StartMenuFolder, HostSelfInstall.ShortcutName);
}

/// <summary>
/// "Launch it, then install it from inside": copies the single-file Host to the person's own program folder, adds a Start menu shortcut and an entry in
/// Settings > Apps, with no administrator rights and no second download. Settings stay where they are (%AppData%), so the installed copy sees the same ones.
/// Removal is the same program started with <see cref="UninstallSwitch"/>, which the Apps entry runs; it keeps the person's settings.
/// Not offered when Windows or winget manages the program (see <see cref="Detect"/>). docs/windows/STORE-AND-WINGET.md section 6.
/// </summary>
public static class HostSelfInstall
{
    public const string ExeName = "ClassroomHost.exe";
    public const string ShortcutName = "Classroom Manager.lnk";
    public const string UninstallSwitch = "--uninstall-self";
    const string ProductPage = "https://dausuva.com/school/";

    public static SelfInstallState Detect(string processPath, bool packaged, SelfInstallPaths paths, IEnumerable<string> programFilesFolders)
    {
        if (packaged) return SelfInstallState.Packaged;
        var folder = Path.GetDirectoryName(Path.GetFullPath(processPath)) ?? "";
        if (File.Exists(Path.Combine(folder, "ClassroomHost.dll"))) return SelfInstallState.MoreThanOneFile;
        if (File.Exists(Path.Combine(folder, HostDataLocation.PortableMarker))) return SelfInstallState.PortableMarker;
        if (IsInside(folder, paths.InstallFolder) || programFilesFolders.Any(programFiles => programFiles.Length > 0 && IsInside(folder, programFiles)))
            return SelfInstallState.AlreadyInstalled;
        if (folder.Contains(@"\Microsoft\WinGet\", StringComparison.OrdinalIgnoreCase)) return SelfInstallState.ManagedByWinget;
        return SelfInstallState.Offered;
    }

    public static SelfInstallState DetectHere() => Detect(Environment.ProcessPath ?? "", ClassroomManager.Shared.PackageIdentity.IsPackaged, SelfInstallPaths.ForThisUser(),
        [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)]);

    static bool IsInside(string folder, string parent)
    {
        var normalizedParent = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
        return (Path.GetFullPath(folder).TrimEnd('\\') + "\\").StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copies <paramref name="sourceProgram"/> into the install folder and registers the shortcut and the Apps entry. Throws IOException when the target is in use.</summary>
    public static void Install(string sourceProgram, string version, SelfInstallPaths paths)
    {
        Directory.CreateDirectory(paths.InstallFolder);
        var temporary = paths.ProgramPath + ".new";
        File.Copy(sourceProgram, temporary, overwrite: true);
        File.Move(temporary, paths.ProgramPath, overwrite: true);
        Directory.CreateDirectory(paths.StartMenuFolder);
        CreateShortcut(paths.ShortcutPath, paths.ProgramPath, paths.InstallFolder);
        using var key = Registry.CurrentUser.CreateSubKey(paths.RegistryKeyPath, writable: true);
        key.SetValue("DisplayName", "Classroom Manager Host");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Dausuva Labs");
        key.SetValue("DisplayIcon", paths.ProgramPath);
        key.SetValue("InstallLocation", paths.InstallFolder);
        key.SetValue("URLInfoAbout", ProductPage);
        key.SetValue("UninstallString", $"\"{paths.ProgramPath}\" {UninstallSwitch}");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>Removes the shortcut and the Apps entry. The program file may be the running one, so the folder is deleted by <see cref="RemoveFolderSoon"/> after this process ends. Settings are not touched.</summary>
    public static void Uninstall(SelfInstallPaths paths)
    {
        if (File.Exists(paths.ShortcutPath)) File.Delete(paths.ShortcutPath);
        Registry.CurrentUser.DeleteSubKeyTree(paths.RegistryKeyPath, throwOnMissingSubKey: false);
    }

    /// <summary>Starts a hidden command that deletes the install folder a few seconds later, when this process has ended.</summary>
    public static void RemoveFolderSoon(SelfInstallPaths paths)
    {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{paths.InstallFolder}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    static void CreateShortcut(string shortcutPath, string target, string workingFolder)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows could not create the Start menu shortcut.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(shortcutPath);
            link.TargetPath = target;
            link.WorkingDirectory = workingFolder;
            link.IconLocation = target;
            link.Description = "Classroom Manager Host";
            link.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    /// <summary>Handles the Apps entry's removal command line. Returns the exit code.</summary>
    public static int RunUninstall()
    {
        var paths = SelfInstallPaths.ForThisUser();
        var answer = MessageBox.Show("Remove Classroom Manager Host from this computer? Your settings and keys are kept.", "Classroom Manager",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return 1;
        Uninstall(paths);
        RemoveFolderSoon(paths);
        return 0;
    }
}
