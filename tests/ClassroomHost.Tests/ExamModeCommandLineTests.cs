// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

/// <summary>Uses the saved settings of a scratch folder and the process-wide "saves suspended" switch, so it never runs beside the backup tests.</summary>
[Collection(nameof(HostBackupTests))]
public sealed class ExamModeCommandLineTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("cm-exam-cli-").FullName;
    string Settings => Path.Combine(_folder, "host.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_command_line_records_the_computers_it_started_and_ended_an_exam_on()
    {
        var settings = new HostSettings();

        ExamModeScope.Record(settings, on: true, [("id-1", "10.0.0.1"), ("", "10.0.0.2")]);
        Assert.True(settings.ExamModeEnabled);
        Assert.True(ExamModeScope.Contains(settings.ExamModeComputers, "id-1", ""));
        Assert.True(ExamModeScope.Contains(settings.ExamModeComputers, "", "10.0.0.2"));

        ExamModeScope.Record(settings, on: false, [("id-1", "10.0.0.1")]);
        Assert.True(settings.ExamModeEnabled);                       // one computer is still in the exam
        Assert.False(ExamModeScope.Contains(settings.ExamModeComputers, "id-1", "10.0.0.1"));

        ExamModeScope.Record(settings, on: false, [("", "10.0.0.2")]);
        Assert.False(settings.ExamModeEnabled);
        Assert.Empty(settings.ExamModeComputers);
    }

    [Fact]
    public void An_open_Host_takes_over_an_exam_another_program_saved_and_does_not_write_over_it()
    {
        // The Host window has its settings in memory.
        var window = new HostSettings { Psk = "LAB11-LAB22-LAB33-LAB44-LAB55", Computers = [new SavedComputer { Address = "10.0.0.1", Name = "PC-01", Room = "410" }] };
        window.Save(Settings);
        Assert.False(window.AdoptExamChangeMadeElsewhere(Settings));    // nothing changed since

        // The command line, another process, turns an exam on and saves.
        var commandLine = HostSettings.Load(Settings);
        ExamModeScope.Record(commandLine, on: true, [("", "10.0.0.1")]);
        File.SetLastWriteTimeUtc(Settings, DateTime.UtcNow.AddSeconds(-5));   // make the next write clearly later
        window.AdoptExamChangeMadeElsewhere(Settings);
        commandLine.Save(Settings);
        File.SetLastWriteTimeUtc(Settings, DateTime.UtcNow.AddSeconds(5));

        // The window saves something of its own before its next refresh: the exam must survive that.
        window.QuickActionMessage = "Changed in the window";
        window.Save(Settings);

        Assert.True(window.ExamModeEnabled);
        var saved = HostSettings.Load(Settings);
        Assert.True(saved.ExamModeEnabled);
        Assert.True(ExamModeScope.Contains(saved.ExamModeComputers, "", "10.0.0.1"));
        Assert.Equal("Changed in the window", saved.QuickActionMessage);
        // And it is not taken over twice.
        Assert.False(window.AdoptExamChangeMadeElsewhere(Settings));
    }

    [Fact]
    public void Updating_clients_from_the_command_line_needs_the_file_and_a_confirmation()
    {
        Assert.Contains("--client", Assert.Throws<CliUsageException>(() => CliParser.Parse(["update", "--all", "--yes"])).Message);
        Assert.Contains("--yes", Assert.Throws<CliUsageException>(() => CliParser.Parse(["update", "--all", "--client", "C:\\x\\ClassroomClient.exe"])).Message);
        Assert.Contains("target", Assert.Throws<CliUsageException>(() => CliParser.Parse(["update", "--client", "C:\\x\\ClassroomClient.exe", "--yes"])).Message);

        var options = CliParser.Parse(["update", "--room", "410", "--client", "\"C:\\x\\ClassroomClient.exe\"", "--force", "--yes"]);
        Assert.Equal(("update", "C:\\x\\ClassroomClient.exe", true, "410"), (options.Command, options.ClientFile, options.Force, options.Room));

        var file = Path.Combine(_folder, "client.bin");
        File.WriteAllText(file, "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", CliUpdate.Sha256(file));
    }
}
