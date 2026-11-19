// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared;

/// <summary>
/// Text the Client shows to the student, in the language of that student's Windows interface.
/// English is the fallback for every language without a translation here.
/// </summary>
public static class ClientText
{
    /// <summary>The notification-area icon's tooltip. Windows cuts a tooltip off at 127 characters.</summary>
    public static string TrayNotice(string? uiCulture) => Language(uiCulture) switch
    {
        "lt" => "Classroom Manager – šį kompiuterį gali valdyti mokytojas",
        _ => "Classroom Manager - this computer can be managed by the teacher",
    };

    /// <summary>The two-letter language of a culture name such as "lt-LT"; empty when there is none.</summary>
    static string Language(string? uiCulture)
    {
        var name = (uiCulture ?? "").Trim();
        var end = name.IndexOfAny(['-', '_']);
        return (end < 0 ? name : name[..end]).ToLowerInvariant();
    }
}
