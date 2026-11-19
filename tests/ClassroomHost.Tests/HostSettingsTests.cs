// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class HostSettingsTests
{
    [Fact]
    public void A_new_Host_starts_in_the_light_theme()
    {
        Assert.Equal("Light", new HostSettings().Theme);
        Assert.Equal(AppTheme.Light, Enum.Parse<AppTheme>(new HostSettings().Theme));
    }

    [Fact]
    public void Save_protects_keys_outside_host_json_and_round_trips_them()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "host.json");
        try
        {
            var settings = new HostSettings
            {
                HostId = "testhost",
                UploadToken = "0123456789abcdef0123456789abcdef",
                Psk = "default-secret",
                PreviousPsks = ["previous-secret"],
                AdditionalHostKeys =
                [
                    new NamedHostKey { Id = "science", Name = "Science lab", Psk = "science-secret" },
                ],
            };
            settings.Save(path);

            var plainJson = File.ReadAllText(path);
            Assert.DoesNotContain("default-secret", plainJson);
            Assert.DoesNotContain("previous-secret", plainJson);
            Assert.DoesNotContain("science-secret", plainJson);
            Assert.DoesNotContain("0123456789abcdef0123456789abcdef", plainJson);
            Assert.Contains("Science lab", plainJson);

            var protectedBytes = File.ReadAllBytes(Path.ChangeExtension(path, ".keys"));
            var protectedText = Encoding.UTF8.GetString(protectedBytes);
            Assert.DoesNotContain("default-secret", protectedText);
            Assert.DoesNotContain("science-secret", protectedText);

            var loaded = HostSettings.Load(path);
            Assert.Equal("default-secret", loaded.Psk);
            Assert.Equal(["previous-secret"], loaded.PreviousPsks);
            var additional = Assert.Single(loaded.AdditionalHostKeys);
            Assert.Equal("Science lab", additional.Name);
            Assert.Equal("science-secret", additional.Psk);
            Assert.Equal("0123456789abcdef0123456789abcdef", loaded.UploadToken);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Load_migrates_legacy_plaintext_keys_to_the_protected_store()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "host.json");
        try
        {
            File.WriteAllText(path,
                """{"psk":"legacy-default","hostId":"legacy","previousPsks":["legacy-old"],"additionalHostKeys":[{"id":"art","name":"Art lab","psk":"legacy-art"}]}""");

            var loaded = HostSettings.Load(path);

            Assert.Equal("legacy-default", loaded.Psk);
            Assert.Equal("legacy-old", Assert.Single(loaded.PreviousPsks));
            Assert.Equal("legacy-art", Assert.Single(loaded.AdditionalHostKeys).Psk);
            Assert.True(File.Exists(Path.ChangeExtension(path, ".keys")));
            var migratedJson = File.ReadAllText(path);
            Assert.DoesNotContain("legacy-default", migratedJson);
            Assert.DoesNotContain("legacy-old", migratedJson);
            Assert.DoesNotContain("legacy-art", migratedJson);
            var backupJson = File.ReadAllText(path + ".bak");
            Assert.DoesNotContain("legacy-default", backupJson);
            Assert.DoesNotContain("legacy-old", backupJson);
            Assert.DoesNotContain("legacy-art", backupJson);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Save_is_atomic_and_load_recovers_the_previous_settings_from_backup()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "host.json");
        try
        {
            var settings = new HostSettings { HostId = "testhost", Psk = "secret", Theme = "Light" };
            settings.Save(path);
            settings.Theme = "Dark";
            settings.Save(path);

            Assert.Contains("Light", File.ReadAllText(path + ".bak"));
            File.WriteAllText(path, "{not valid json");

            var recovered = HostSettings.Load(path);

            Assert.Equal("Light", recovered.Theme);
            Assert.Equal("secret", recovered.Psk);
            Assert.Contains("Light", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Save_reports_persistence_errors_instead_of_suppressing_them()
    {
        var directory = TempDirectory();
        try
        {
            var fileWhereDirectoryIsRequired = Path.Combine(directory, "blocked");
            File.WriteAllText(fileWhereDirectoryIsRequired, "not a directory");
            var settings = new HostSettings { HostId = "testhost", Psk = "secret" };

            Assert.ThrowsAny<IOException>(() => settings.Save(Path.Combine(fileWhereDirectoryIsRequired, "host.json")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Load_upgrades_the_legacy_lock_only_quick_action_default_once()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "host.json");
        try
        {
            File.WriteAllText(path, """{"hostId":"testhost","quickActions":["lock"]}""");

            var loaded = HostSettings.Load(path);

            Assert.Equal(["lock", "unlock", "send-files"], loaded.QuickActions);
            Assert.Equal(2, loaded.QuickActionDefaultsVersion);

            loaded.QuickActions = ["lock"];
            loaded.Save(path);
            Assert.Equal(["lock"], HostSettings.Load(path).QuickActions);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Save_round_trips_host_and_user_interface_settings()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "host.json");
        try
        {
            var settings = new HostSettings
            {
                HostId = "testhost",
                Psk = "secret",
                DefaultKeyName = "Room 410",
                AutoFinishKeyRotation = false,
                RunSchedules = false,
                TcpPort = 43110,
                UdpPort = 43111,
                DiscoverySubnet = "10.40.0.0/24",
                UseTls = false,
                Theme = "Dark",
                ButtonDisplay = "IconsOnly",
                CrashLoggingEnabled = false,
                AutoRefreshEnabled = false,
                HiddenComputerColumns = ["Ping", "Interface language"],
                ExamModeEnabled = true,
                ExamModeDomains = ["example.test"],
                ExamModeApps = ["browser.exe"],
                ExamModeMuteAudio = true,
                ExamModeBlockUsbStorage = true,
                DefaultWebsiteBlocklist = [" WWW.Example.test ", ".school.test"],
                QuickActions = ["unlock"],
                QuickActionMessage = "Exam complete",
                QuickActionDefaultsVersion = 2,
                WindowFilters = new Dictionary<string, WindowFilterSettings>
                {
                    ["ProcessesWindow"] = new()
                    {
                        Text = "browser",
                        Selections = new() { ["Owner"] = "User", ["View"] = "Signed-in user session" },
                    },
                    ["BulkProgramsWindow"] = new() { HiddenColumns = [" Publisher ", "publisher", "", "Silent"] },
                },
                HostMode = HostModes.Admin,
                TeacherVisibleCommands = ["computers.scan"],
                TeacherDefaultsVersion = TeacherCommandCatalog.AddedDefaults.Max(added => added.Version),
                Computers =
                [
                    new SavedComputer
                    {
                        Address = "10.40.0.15", Name = "LAB-PC", ClientId = "client-15",
                        Mac = "001122AABBCC", Room = "410",
                    },
                ],
            };

            settings.Save(path);
            var loaded = HostSettings.Load(path);

            Assert.Equal("Room 410", loaded.DefaultKeyName);
            Assert.False(loaded.AutoFinishKeyRotation);
            Assert.False(loaded.RunSchedules);
            Assert.Equal(43110, loaded.TcpPort);
            Assert.Equal(43111, loaded.UdpPort);
            Assert.Equal("10.40.0.0/24", loaded.DiscoverySubnet);
            Assert.True(loaded.UseTls);          // the file said off; TLS cannot be turned off (T-186)
            Assert.True(loaded.TlsWasOff);
            Assert.Equal("Dark", loaded.Theme);
            Assert.Equal("IconsOnly", loaded.ButtonDisplay);
            Assert.False(loaded.CrashLoggingEnabled);
            Assert.False(loaded.AutoRefreshEnabled);
            Assert.Equal(["Ping", "Interface"], loaded.HiddenComputerColumns);
            Assert.True(loaded.ExamModeEnabled);
            Assert.Equal(["example.test"], loaded.ExamModeDomains);
            Assert.Equal(["browser.exe"], loaded.ExamModeApps);
            Assert.True(loaded.ExamModeMuteAudio);
            Assert.False(loaded.ExamModeDisableCamera);
            Assert.True(loaded.ExamModeBlockUsbStorage);
            Assert.Equal(["example.test", "school.test"], loaded.DefaultWebsiteBlocklist);
            Assert.Equal(["unlock"], loaded.QuickActions);
            Assert.Equal("Exam complete", loaded.QuickActionMessage);
            var processFilter = loaded.FilterState("ProcessesWindow");
            Assert.Equal("browser", processFilter.Text);
            Assert.Equal("User", processFilter.Selection("Owner", ""));
            Assert.Equal("Signed-in user session", processFilter.Selection("View", ""));
            Assert.Empty(processFilter.HiddenColumns);
            Assert.Equal(["Publisher", "Silent"], loaded.FilterState("BulkProgramsWindow").HiddenColumns);
            Assert.Equal(HostModes.Admin, loaded.HostMode);
            Assert.Equal(["computers.scan"], loaded.TeacherVisibleCommands);
            var computer = Assert.Single(loaded.Computers);
            Assert.Equal("client-15", computer.ClientId);
            Assert.Equal("001122AABBCC", computer.Mac);
            Assert.Equal("410", computer.Room);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Load_normalizes_hidden_computer_columns_without_hiding_selection()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "host.json");
        try
        {
            File.WriteAllText(path,
                """{"hostId":"testhost","hiddenComputerColumns":[" Ping ","ping","Signed-in user","Clock vs host","Windows","Interface language","Blocked websites","Running apps / open windows","Client build","Last seen","Select","",null]}""");

            var loaded = HostSettings.Load(path);

            Assert.Equal(["Ping", "User", "Clock", "OS Version", "Interface", "Hosts", "Apps", "Client", "Last Seen"], loaded.HiddenComputerColumns);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Save_round_trips_content_verified_transfer_history()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "host.json");
        try
        {
            var settings = new HostSettings
            {
                HostId = "testhost",
                UploadToken = "0123456789abcdef0123456789abcdef",
                FileTransferHistory =
                [
                    new FileTransferRecord
                    {
                        Id = "transfer-1",
                        Computer = "PC-1",
                        Address = "10.0.0.1",
                        FileName = "lesson.txt",
                        DestinationPath = @"C:\Users\Student\Desktop\lesson.txt",
                        Size = 42,
                        Sha256 = new string('a', 64),
                    },
                ],
            };

            settings.Save(path);
            var record = Assert.Single(HostSettings.Load(path).FileTransferHistory);

            Assert.Equal("transfer-1", record.Id);
            Assert.Equal(42, record.Size);
            Assert.True(record.CanUndo);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Launch_history_keeps_pinned_items_and_bounds_recent_items()
    {
        var settings = new HostSettings();
        settings.RememberWebsite("https://saved.test/", true);
        for (var i = 0; i < 12; i++) settings.RememberWebsite($"https://recent{i}.test/", false);
        settings.RememberProgram("lesson.exe", "--start", true);
        for (var i = 0; i < 12; i++) settings.RememberProgram($"app{i}.exe", "", false);

        Assert.Equal(["https://saved.test/"], settings.SavedWebsites);
        Assert.Equal(10, settings.RecentWebsites.Count);
        Assert.Equal("https://recent11.test/", settings.RecentWebsites[0]);
        Assert.Equal("lesson.exe", Assert.Single(settings.SavedPrograms).Program);
        Assert.Equal(10, settings.RecentPrograms.Count);
        Assert.Equal("app11.exe", settings.RecentPrograms[0].Program);
    }

    static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ClassroomHost.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
