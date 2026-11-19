// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>The delay the Power form suggests for each action.</summary>
public static class PowerDelays
{
    /// <summary>
    /// A restart is usually asked for when a computer needs it now; a shutdown gives students a minute
    /// to save. A delay under <see cref="UndoMinimum"/> seconds cannot be undone from the Operations view.
    /// </summary>
    public static string Default(string action) => action switch
    {
        "Restart" => "5",
        _ => "60",
    };

    /// <summary>The shortest delay for which a pending shutdown or restart is offered for Undo.</summary>
    public const int UndoMinimum = 20;
}
