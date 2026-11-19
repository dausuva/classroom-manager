// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Windows;
using ClassroomManager.Shared;

namespace ClassroomManager.Host;

/// <summary>Explicit local-only creation; never interprets client paths or creates missing parents.</summary>
public static class LocalFolderCreation
{
    public static string Create(string parent, string name)
    {
        parent = PathText.Clean(parent);
        name = name.Trim();
        if (!Path.IsPathFullyQualified(parent) || !Directory.Exists(parent))
            throw new IOException("Choose an existing folder first.");
        if (name.Length == 0 || name is "." or ".." || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("Enter a single valid folder name.");
        var path = Path.Combine(parent, name);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("That name already exists.");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Prompt(Window owner, Func<string> parent, Action<string> select)
    {
        var dialog = new FormDialog("Create new folder", owner)
            .Note(parent(), preserveText: true).Text("name", "Folder name")
            .Require("name", "Enter a folder name.");
        if (!dialog.Ask("Create")) return;
        try { select(Create(parent(), dialog.S("name"))); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(owner, Loc.T(exception.Message), Loc.T("Create new folder"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
