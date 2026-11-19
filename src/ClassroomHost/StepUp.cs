// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Raised when the person did not allow a dangerous action; shown like any other error of that computer.</summary>
public sealed class StepUpDeniedException : Exception
{
    public StepUpDeniedException(string message) : base(message) { }
}

/// <summary>What the person is asked, and what the answer can be.</summary>
public sealed record StepUpRequest(string Command, string Action, string Computer);

public enum StepUpAnswer { Deny, Allow }

/// <summary>
/// Step-up for dangerous Admin actions (T-190). The Host window asks once before it sends a command that changes who can reach a computer, runs code
/// on it, removes software or accounts, or edits the registry, and then allows them for ten minutes. It is a check that a person is at the Host and means
/// it, not a second secret: the window cannot protect a Host whose keys were stolen. Where nobody can be asked (the command line, which needs --yes, the
/// scheduler and the Health tools) no prompt is set and nothing is asked.
/// </summary>
public static class StepUp
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>Commands that ask. Each needs an Admin key on the computer, which a test checks.</summary>
    public static readonly IReadOnlyDictionary<string, string> Dangerous = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Cmd.CmdRun] = "run a command",
        [Cmd.TermStart] = "open a command window",
        [Cmd.InstallRun] = "run an installer",
        [Cmd.InstallWinget] = "install or remove a program with winget",
        [Cmd.ProgramsUninstall] = "uninstall a program",
        [Cmd.SelfUpdate] = "replace the Client program",
        [Cmd.ClientUninstall] = "remove the Client from the computer",
        [Cmd.SystemRename] = "rename the computer",
        [Cmd.KeySet] = "change the lab key",
        [Cmd.AccessKeysSet] = "add or change an access key",
        [Cmd.AccessKeysRemove] = "remove an access key",
        [Cmd.AccountCreate] = "create a Windows account",
        [Cmd.AccountSetPassword] = "set an account password",
        [Cmd.AccountDisable] = "disable an account",
        [Cmd.AccountDelete] = "delete an account",
        [Cmd.AccountIdentityRemove] = "remove a signed-in account",
        [Cmd.RegistrySetValue] = "change the registry",
        [Cmd.RegistryDeleteValue] = "delete a registry value",
        [Cmd.RegistryDeleteKey] = "delete a registry key",
        [Cmd.RegistryImport] = "import registry settings",
        [Cmd.TasksDelete] = "delete a scheduled task",
        [Cmd.AutorunRemove] = "remove a startup entry",
        [Cmd.ProcKill] = "end a program by force",
        [Cmd.RecycleBinEmpty] = "empty the Recycle Bin",
        [Cmd.BrowserDataClear] = "erase browser data",
        [Cmd.BrowserProfilesRemove] = "remove browser profiles",
        [Cmd.WindowsUpdateRestart] = "restart the computer for updates",
        [Cmd.WindowsUpdateInstall] = "install Windows updates",
        [Cmd.TasksRun] = "run a scheduled task now",
    };

    /// <summary>For tests: the time the window is measured against.</summary>
    public static Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>Set by the Host window; null where nobody can be asked.</summary>
    public static Func<StepUpRequest, Task<StepUpAnswer>>? Prompt { get; set; }

    static readonly SemaphoreSlim Gate = new(1, 1);
    static DateTime _unlockedUntil = DateTime.MinValue;

    public static bool IsUnlocked => Now() < _unlockedUntil;

    /// <summary>Allows dangerous actions for the window, when a person has just confirmed something at least as strong (the typed key change).</summary>
    public static void Grant() => _unlockedUntil = Now() + Window;

    public static void Lock() => _unlockedUntil = DateTime.MinValue;

    public static async Task EnsureAsync(string command, string computer, CancellationToken ct = default)
    {
        if (Prompt is not { } prompt || !Dangerous.TryGetValue(command, out var action) || IsUnlocked) return;
        // Many computers send at once; the first call asks and the rest wait for the answer.
        await Gate.WaitAsync(ct);
        try
        {
            if (IsUnlocked) return;
            var answer = await prompt(new StepUpRequest(command, action, computer));
            if (answer == StepUpAnswer.Deny) throw new StepUpDeniedException($"Not sent: you did not allow the action \"{Loc.T(action)}\".");
            if (answer == StepUpAnswer.Allow) Grant();
        }
        finally { Gate.Release(); }
    }
}
