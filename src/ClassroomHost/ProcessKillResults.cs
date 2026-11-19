// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

/// <summary>
/// Sorts the client's answers to "end this process". Ending a program's main process often takes its other
/// processes with it, so the next ones in the selection are already gone by the time they are asked for. The
/// client then says the id is unknown, or belongs to another process now: either way the process the teacher
/// chose has ended, which is what was wanted, and it is not an error to show.
/// </summary>
public static class ProcessKillResults
{
    public static bool AlreadyGone(string message) =>
        message.Contains("No process with that id", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("now belongs to", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("has been reused by another", StringComparison.OrdinalIgnoreCase);
}
