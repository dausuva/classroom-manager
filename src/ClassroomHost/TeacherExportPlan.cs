// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json.Nodes;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>
/// What the Export teacher settings dialog decides (T-253 phase 2): whether the name and passwords may be used, the Teacher key that is created, the computers written to
/// the file and the closing texts. The dialogs, the file picker and the log line stay in the window so they keep their translations.
/// </summary>
public static class TeacherExportPlan
{
    public const string Caption = "Export teacher settings";

    /// <summary>Why the name or the passwords cannot be used, or null when they can.</summary>
    public static string? Problem(string name, string password, string again)
    {
        if (!AccessKeyNames.IsAcceptable(name, out var reason)) return reason;
        return password != again ? "The two passwords are not the same." : null;
    }

    /// <summary>A file without a password is allowed only after the user says so.</summary>
    public static bool NeedsNoPasswordConfirmation(string password) => password.Length == 0;

    public static string Title(string name) => $"Add Teacher access key '{name}'";

    public static JsonObject Arguments(string name, string key) =>
        new() { ["name"] = name, ["role"] = nameof(OperatorRole.Teacher), ["key"] = key };

    public static List<SavedComputer> Computers(IEnumerable<ClientInfo> targets) => targets.Select(c => new SavedComputer
    {
        Address = c.Address, Name = c.ComputerName, Alias = c.Alias, ClientId = c.ClientId, Mac = ComputerIdentity.NormalizeMac(c.Mac), Port = c.Port, Room = c.Room,
    }).ToList();

    public static string WriteFailed(string name, string message) =>
        $"The key '{name}' is on the computers, but the file could not be written: {message}\n\nExport again with the same name to replace the key and write a new file.";

    public static string Summary(string file, string name, int accepted, int total, bool passwordSet)
    {
        var missed = total - accepted;
        return $"Saved {file}\n\nThe Teacher key '{name}' is on {accepted} of {total} computer(s)." +
               (missed > 0 ? $" {missed} did not take it (see Operations); exporting again with the same name later is safe." : "") +
               (passwordSet ? "\n\nGive the teacher the file, and the password by another way." : "\n\nThe file is not protected: hand it over privately and delete copies you do not need.");
    }
}
