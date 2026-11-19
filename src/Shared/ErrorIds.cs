// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.RegularExpressions;

namespace ClassroomManager.Shared;

/// <summary>Puts an error id at the end of a message and reads it back (T-134); see <see cref="ErrorCatalog"/>.</summary>
public static class ErrorIds
{
    static readonly Regex Tagged = new(@" \(error code ([0-9A-F]{6})\)$", RegexOptions.CultureInvariant);

    /// <summary>The message with its error id at the end, unless it already has one.</summary>
    public static string WithId(string message, ErrorInfo error) => Tagged.IsMatch(message) ? message : message + Tag(error.Id);

    /// <summary>The id as it is shown; translated where the message is shown, so it reads "(klaidos kodas 020001)" in Lithuanian.</summary>
    public static string Tag(string id) => $" (error code {id})";

    /// <summary>Splits "message (error code 020001)" into the message and the id; false when the text has no id at its end.</summary>
    public static bool TrySplit(string? text, out string message, out string id)
    {
        var match = text is null ? Match.Empty : Tagged.Match(text);
        message = match.Success ? text![..match.Index] : text ?? "";
        id = match.Success ? match.Groups[1].Value : "";
        return match.Success;
    }
}
