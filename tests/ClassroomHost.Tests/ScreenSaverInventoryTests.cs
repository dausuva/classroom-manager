// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json.Nodes;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class ScreenSaverInventoryTests
{
    static ScreenSaverState State(bool active, string name, params string[] available) =>
        new(active, name, 600, false, false, false, available);

    [Fact]
    public void State_is_read_from_the_client_response_with_windows_and_managed_files_merged()
    {
        var data = JsonNode.Parse("""
            {
              "active": true, "path": "C:\\Users\\s\\AppData\\Local\\ClassroomManager\\ScreenSavers\\MatrixGeometry.scr",
              "name": "MatrixGeometry.scr", "timeoutSec": 300, "secure": true, "policyManaged": false, "canRestore": true,
              "installed": [ { "name": "Bubbles.scr", "path": "C:\\Windows\\System32\\Bubbles.scr" } ],
              "managed": [ { "name": "MatrixGeometry.scr", "path": "x" }, { "name": "bubbles.scr", "path": "y" } ]
            }
            """)!.AsObject();

        var state = ScreenSaverState.FromJson(data);

        Assert.True(state.Active);
        Assert.Equal("MatrixGeometry.scr", state.Name);
        Assert.Equal(300, state.TimeoutSec);
        Assert.True(state.Secure);
        Assert.True(state.CanRestore);
        Assert.Equal(["Bubbles.scr", "MatrixGeometry.scr"], state.Available);
    }

    [Fact]
    public void Only_screen_savers_present_on_every_answering_computer_can_be_chosen_by_name()
    {
        var common = ScreenSaverInventory.CommonNames(
        [
            State(false, "", "Ribbons.scr", "Bubbles.scr", "MatrixGeometry.scr"),
            State(false, "", "bubbles.scr", "Ribbons.scr"),
            State(false, "", "Mystify.scr", "Bubbles.scr", "Ribbons.scr"),
        ]);

        Assert.Equal(["Bubbles.scr", "Ribbons.scr"], common);
        Assert.Empty(ScreenSaverInventory.CommonNames([]));
    }

    [Fact]
    public void Summary_groups_computers_by_what_is_currently_active()
    {
        var summary = ScreenSaverInventory.Summarize(
        [
            State(true, "MatrixGeometry.scr"),
            State(false, "Bubbles.scr"),
            State(true, "matrixgeometry.scr"),
            State(false, ""),
            State(true, "Ribbons.scr"),
        ]);

        Assert.Equal("MatrixGeometry.scr on 2 · off on 2 · Ribbons.scr on 1", summary);
    }

    [Theory]
    [InlineData("10", 600)]
    [InlineData(" 1 ", 60)]
    [InlineData("9999", 599940)]
    public void Idle_minutes_are_converted_to_seconds(string text, int seconds)
    {
        Assert.True(ScreenSaverInventory.TryParseMinutes(text, out var parsed));
        Assert.Equal(seconds, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("10000")]
    [InlineData("1.5")]
    [InlineData("ten")]
    public void Idle_minutes_outside_what_Windows_offers_are_rejected(string text)
    {
        Assert.False(ScreenSaverInventory.TryParseMinutes(text, out _));
    }

    [Fact]
    public void A_program_file_named_scr_is_described_with_its_hash_and_no_signer()
    {
        var directory = Directory.CreateTempSubdirectory("cm-screensaver-").FullName;
        try
        {
            var path = Path.Combine(directory, "Sample.scr");
            File.WriteAllBytes(path, [(byte)'M', (byte)'Z', 1, 2, 3]);

            var file = ScreenSaverFile.Inspect(path, out var problem);

            Assert.NotNull(file);
            Assert.Equal("", problem);
            Assert.Equal("Sample.scr", file.Name);
            Assert.Equal(5, file.Length);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                file.Sha256);
            Assert.False(file.IsSigned);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Files_that_cannot_be_screen_savers_are_refused_with_a_reason()
    {
        var directory = Directory.CreateTempSubdirectory("cm-screensaver-").FullName;
        try
        {
            var notAProgram = Path.Combine(directory, "Picture.scr");
            File.WriteAllText(notAProgram, "not a program");
            var wrongExtension = Path.Combine(directory, "Program.exe");
            File.WriteAllBytes(wrongExtension, [(byte)'M', (byte)'Z']);

            Assert.Null(ScreenSaverFile.Inspect(notAProgram, out var notProgramProblem));
            Assert.Contains("not a Windows screen saver", notProgramProblem);
            Assert.Null(ScreenSaverFile.Inspect(wrongExtension, out var extensionProblem));
            Assert.Contains(".scr", extensionProblem);
            Assert.Null(ScreenSaverFile.Inspect(Path.Combine(directory, "Missing.scr"), out var missingProblem));
            Assert.Contains("not found", missingProblem);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
