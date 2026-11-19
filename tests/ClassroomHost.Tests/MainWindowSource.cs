// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;

namespace ClassroomHost.Tests;

/// <summary>
/// The source text of <c>MainWindow</c>, which is one partial class spread over several files (<c>MainWindow.xaml.cs</c> and <c>MainWindow.*.cs</c>).
/// Tests that check what the window does or does not contain read it through here, so a move between the files does not change their answer.
/// </summary>
static class MainWindowSource
{
    public static string Folder => Path.Combine(StringExtractor.RepositoryRoot(), "src", "ClassroomHost");

    public static IReadOnlyList<string> Files() => Directory.GetFiles(Folder, "MainWindow*.cs")
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();

    public static string ReadAll() => string.Join("\n", Files().Select(File.ReadAllText));
}
