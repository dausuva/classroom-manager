// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Text.Json;

namespace ClassroomManager.Shared;

/// <summary>Omits known credential values from support text; does not classify arbitrary user text.</summary>
public static class DiagnosticText
{
    public const string Omitted = "[REDACTED]";

    public static string OmitSecrets(string text, IEnumerable<string> secrets)
    {
        var values = secrets.Where(value => !string.IsNullOrEmpty(value) && value.Length <= text.Length)
            .Distinct(StringComparer.Ordinal).SelectMany(value => new[]
            {
                value, JsonEncodedText.Encode(value).ToString(), Uri.EscapeDataString(value)
            }).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length).ToArray();
        foreach (var value in values) text = text.Replace(value, Omitted, StringComparison.Ordinal);
        return text;
    }
}
