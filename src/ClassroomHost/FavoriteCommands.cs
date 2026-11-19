// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

public static class FavoriteCommandShells
{
    public const string CommandPrompt = "cmd";
    public const string PowerShell = "powershell";
}

public sealed class FavoriteCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Shell { get; set; } = FavoriteCommandShells.CommandPrompt;
    public string Level { get; set; } = Levels.System;
    public string Command { get; set; } = "";

    public string ShellText => Shell == FavoriteCommandShells.PowerShell ? "PowerShell" : "cmd";
    public string LevelText => Level == Levels.User ? "Signed-in user" : "SYSTEM";
}

public static class FavoriteCommandCatalog
{
    public static List<FavoriteCommand> Normalize(IEnumerable<FavoriteCommand>? commands)
    {
        var result = new List<FavoriteCommand>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands ?? [])
        {
            if (command is null || string.IsNullOrWhiteSpace(command.Name) || string.IsNullOrWhiteSpace(command.Command))
                continue;

            var id = command.Id?.Trim() ?? "";
            if (id.Length == 0 || !ids.Add(id))
            {
                do id = Guid.NewGuid().ToString("N"); while (!ids.Add(id));
            }

            result.Add(new FavoriteCommand
            {
                Id = id,
                Name = command.Name.Trim(),
                Shell = string.Equals(command.Shell, FavoriteCommandShells.PowerShell, StringComparison.OrdinalIgnoreCase)
                    ? FavoriteCommandShells.PowerShell
                    : FavoriteCommandShells.CommandPrompt,
                Level = string.Equals(command.Level, Levels.User, StringComparison.OrdinalIgnoreCase) ? Levels.User : Levels.System,
                Command = command.Command.Trim(),
            });
        }
        return result;
    }

    public static List<FavoriteCommand> UpsertByName(
        IEnumerable<FavoriteCommand>? commands,
        FavoriteCommand command)
    {
        var result = Normalize(commands);
        var normalized = Normalize([command]).Single();
        var index = result.FindIndex(item => item.Name.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            result.Add(normalized);
        else
        {
            normalized.Id = result[index].Id;
            result[index] = normalized;
        }
        return result;
    }
}
