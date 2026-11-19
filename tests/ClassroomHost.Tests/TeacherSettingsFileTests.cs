// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

public class TeacherSettingsFileTests
{
    const string AdminKey = "ADMIN-KEY11-KEY22-KEY33-KEY44";
    const string OtherLabKey = "OTHER-LAB11-LAB22-LAB33-LAB44";

    static readonly string TeacherKey = SharedKey.Generate();

    static HostSettings Administrator() => new()
    {
        Psk = AdminKey,
        AdditionalHostKeys = [new NamedHostKey { Name = "Other lab", Psk = OtherLabKey }],
        TcpPort = 47000, UdpPort = 47001, UseTls = false,
        ExamModeDomains = ["games.example.net"], ExamModeApps = ["steam"], ExamModeMuteAudio = true,
        BlockedAppCatalog = ["discord"], DefaultWebsiteBlocklist = ["social.example.org"],
        SavedWebsites = ["https://school.example.org"],
        SavedPrograms = [new SavedProgramLaunch { Program = "notepad.exe", Arguments = "" }],
        QuickActions = ["lock", "unlock"], QuickActionMessage = "Eyes on the board.",
        TeacherVisibleCommands = ["classroom.lock", "classroom.unlock"],
        CustomQuickActions = [new CustomQuickAction { Id = "c1", Name = "Open the textbook", Kind = CustomQuickActionKinds.OpenWebsite, Target = "https://books.example.org" }],
    };

    static SavedComputer[] Room() =>
    [
        new SavedComputer { Address = "10.0.0.11", Name = "PC-01", ClientId = "a1", Mac = "AABBCCDDEE01", Room = "410", KeyId = SharedKey.Fingerprint(AdminKey) },
        new SavedComputer { Address = "10.0.0.12", Name = "PC-02", ClientId = "a2", Mac = "AABBCCDDEE02", Room = "410", KeyId = SharedKey.Fingerprint(AdminKey) },
    ];

    static TeacherSettings Exported(bool presets = true) =>
        TeacherSettingsFile.Create(Administrator(), Room(), "Room 410 teacher", TeacherKey, presets);

    [Fact]
    public void The_file_carries_the_teacher_key_and_never_the_administrators_keys()
    {
        var text = TeacherSettingsFile.Write(Exported(), null);

        Assert.Contains(TeacherKey, text);
        Assert.DoesNotContain(AdminKey, text);
        Assert.DoesNotContain(OtherLabKey, text);
        // The teacher's Host is pointed at the Teacher key for these computers, not at the administrator's.
        Assert.DoesNotContain(SharedKey.Fingerprint(AdminKey), text);
        Assert.False(TeacherSettingsFile.IsProtected(text));

        var read = TeacherSettingsFile.Read(text, null);
        Assert.Equal("Room 410 teacher", read.KeyName);
        Assert.Equal(TeacherKey, read.Key);
        Assert.Equal((47000, 47001, false), (read.TcpPort, read.UdpPort, read.UseTls));
        Assert.Equal(["PC-01", "PC-02"], read.Computers.Select(computer => computer.Name));
        Assert.All(read.Computers, computer => Assert.Equal(SharedKey.Fingerprint(TeacherKey), computer.KeyId));
        Assert.Equal(["games.example.net"], read.Presets!.ExamModeDomains);
    }

    [Fact]
    public void A_password_hides_everything_and_only_that_password_opens_the_file()
    {
        var text = TeacherSettingsFile.Write(Exported(), "correct horse");

        Assert.True(TeacherSettingsFile.IsProtected(text));
        Assert.DoesNotContain(TeacherKey, text);
        Assert.DoesNotContain("PC-01", text);
        Assert.DoesNotContain("games.example.net", text);

        Assert.Equal(TeacherKey, TeacherSettingsFile.Read(text, "correct horse").Key);
        Assert.Contains("password", Assert.Throws<InvalidDataException>(() => TeacherSettingsFile.Read(text, "wrong")).Message);
        Assert.Contains("password", Assert.Throws<InvalidDataException>(() => TeacherSettingsFile.Read(text, null)).Message);
        // Two exports of the same settings do not look alike.
        Assert.NotEqual(text, TeacherSettingsFile.Write(Exported(), "correct horse"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[{\"address\":\"10.0.0.11\",\"name\":\"PC-01\"}]")]                          // a computer list from Export list...
    [InlineData("{\"format\":\"something-else\",\"version\":1}")]
    [InlineData("{\"format\":\"classroom-teacher-settings\",\"version\":99,\"settings\":{}}")] // from a newer version
    [InlineData("{\"format\":\"classroom-teacher-settings\",\"version\":1,\"settings\":{\"key\":\"short\"}}")]
    public void Other_files_are_refused_with_a_message(string text)
    {
        Assert.NotEmpty(Assert.Throws<InvalidDataException>(() => TeacherSettingsFile.Read(text, null)).Message);
    }

    [Fact]
    public void A_new_host_takes_the_key_as_its_own_with_the_connection_settings()
    {
        var teacher = new HostSettings();

        var changes = TeacherSettingsFile.Apply(Exported(), teacher, presets: true, interfaceChoices: true);

        Assert.Equal(TeacherKey, teacher.Psk);
        Assert.Equal("Room 410 teacher", teacher.DefaultKeyName);
        Assert.Empty(teacher.AdditionalHostKeys);
        Assert.Equal((47000, 47001, true), (teacher.TcpPort, teacher.UdpPort, teacher.UseTls));   // a file cannot turn TLS off (T-186)
        Assert.Contains("games.example.net", teacher.ExamModeDomains);
        Assert.True(teacher.ExamModeMuteAudio);
        Assert.Contains(teacher.SavedPrograms, program => program.Program == "notepad.exe");
        Assert.Equal(HostModes.Teacher, teacher.HostMode);
        Assert.Equal(["classroom.lock", "classroom.unlock"], teacher.TeacherVisibleCommands);
        Assert.Equal("Eyes on the board.", teacher.QuickActionMessage);
        Assert.Equal("Open the textbook", Assert.Single(teacher.CustomQuickActions).Name);
        Assert.Equal(4, changes.Count);
    }

    [Fact]
    public void A_host_that_already_has_keys_and_lists_keeps_them()
    {
        var teacher = new HostSettings
        {
            Psk = "MINE1-MINE2-MINE3-MINE4-MINE5", DefaultKeyName = "Room 410 teacher",
            ExamModeDomains = ["mine.example.com"], HostMode = HostModes.Admin, QuickActionMessage = "Mine.",
            TeacherVisibleCommands = ["classroom.message"],
        };

        TeacherSettingsFile.Apply(Exported(), teacher, presets: true, interfaceChoices: false);

        Assert.Equal("MINE1-MINE2-MINE3-MINE4-MINE5", teacher.Psk);
        var added = Assert.Single(teacher.AdditionalHostKeys);
        Assert.Equal(TeacherKey, added.Psk);
        Assert.Equal("Room 410 teacher (2)", added.Name);     // the name was taken by the key already here
        Assert.Equal(["mine.example.com", "games.example.net"], teacher.ExamModeDomains);
        // The toolbar was not asked for.
        Assert.Equal(HostModes.Admin, teacher.HostMode);
        Assert.Equal(["classroom.message"], teacher.TeacherVisibleCommands);
        Assert.Equal("Mine.", teacher.QuickActionMessage);

        // Importing the same file again changes nothing more.
        var again = TeacherSettingsFile.Apply(Exported(), teacher, presets: false, interfaceChoices: false);
        Assert.Single(teacher.AdditionalHostKeys);
        Assert.Contains("already stored", Assert.Single(again));
    }

    [Fact]
    public void Presets_are_left_out_when_not_exported_or_not_wanted()
    {
        var withoutPresets = TeacherSettingsFile.Read(TeacherSettingsFile.Write(Exported(presets: false), null), null);
        Assert.Null(withoutPresets.Presets);

        var teacher = new HostSettings();
        TeacherSettingsFile.Apply(Exported(), teacher, presets: false, interfaceChoices: false);
        Assert.Empty(teacher.ExamModeDomains);
        Assert.Empty(teacher.SavedPrograms);
    }

    [Fact]
    public void The_summary_names_what_is_inside_without_the_key()
    {
        var summary = TeacherSettingsFile.Describe(Exported());

        Assert.Contains("Room 410 teacher", summary);
        Assert.Contains(SharedKey.Fingerprint(TeacherKey), summary);
        Assert.Contains("Computers: 2 in 410", summary);
        Assert.Contains("TLS off", summary);
        Assert.DoesNotContain(TeacherKey, summary);
    }
}
