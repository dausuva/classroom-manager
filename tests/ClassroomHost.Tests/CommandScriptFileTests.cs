// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class CommandScriptFileTests
{
    [Theory]
    [InlineData("lesson.ps1", "powershell")]
    [InlineData("lesson.txt", "powershell")]
    [InlineData("lesson.cmd", "cmd")]
    [InlineData("lesson.BAT", "cmd")]
    public void Load_preserves_text_and_selects_shell_from_extension(string name, string expectedShell)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ClassroomHost.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        try
        {
            File.WriteAllText(path, "Write-Output 'classroom'\n");

            var loaded = CommandScriptFile.Load(path);

            Assert.Equal("Write-Output 'classroom'\n", loaded.Text);
            Assert.Equal(expectedShell, loaded.Shell);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
