// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Shared.Tests;

/// <summary>The repository folder, found from the test program's folder (the one that holds ClassroomManager.sln), for tests that read a file kept beside the source.</summary>
static class RepositoryFiles
{
    public static string Root()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "ClassroomManager.sln"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("The repository folder (with ClassroomManager.sln) was not found above " + AppContext.BaseDirectory);
    }
}
