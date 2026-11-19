// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Windows.Controls;

namespace ClassroomManager.Host;

/// <summary>Bounds long-lived text surfaces so normal operation cannot grow the Host indefinitely.</summary>
public static class UiBufferLimits
{
    public const int ActivityEntries = 2_000;
    public const int TerminalCharacters = 1_000_000;
    public const int CommandCharacters = 2_000_000;
    public const int TerminalHistoryEntries = 200;

    public static string Tail(string value, int maximumCharacters)
    {
        if (maximumCharacters < 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        return value.Length <= maximumCharacters ? value : value[^maximumCharacters..];
    }

    public static void Append(TextBox textBox, string value, int maximumCharacters)
    {
        textBox.AppendText(value);
        if (textBox.Text.Length <= maximumCharacters) return;
        textBox.Text = Tail(textBox.Text, maximumCharacters);
        textBox.CaretIndex = textBox.Text.Length;
    }
}
