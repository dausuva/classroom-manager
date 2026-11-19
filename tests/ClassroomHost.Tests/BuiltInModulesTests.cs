// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.IO;
using System.Text.Json;
using ClassroomManager.Host;
using Xunit;

namespace ClassroomHost.Tests;

public sealed class BuiltInModulesTests
{
    [Fact]
    public async Task Disabling_after_admission_drains_the_sweep_but_blocks_the_next_one()
    {
        var root = Directory.CreateTempSubdirectory("module-drain-").FullName;
        try
        {
            var settings = new HostSettings { Schedules = [new() { Id = "a", Action = "lock", Days = ["Mon"] },
                new() { Id = "b", Action = "lock", Days = ["Mon"] }] };
            var admitted = BuiltInModules.AdmittedSchedules(settings);
            var workflow = new ScheduleWorkflow(Path.Combine(root, "daily.json"), Path.Combine(root, "computers.json"));
            var dispatched = new List<string>();
            Assert.Equal(2, await workflow.RunTimedAsync(admitted, new DateTime(2026, 10, 26, 8, 0, 0), (schedule, _) =>
            {
                dispatched.Add(schedule.Id);
                settings.DisabledModules = [BuiltInModules.Automation];
                return Task.CompletedTask;
            }, (_, _) => throw new Exception()));
            Assert.Equal(["a", "b"], dispatched);
            Assert.Empty(BuiltInModules.AdmittedSchedules(settings));
            var before = File.ReadAllBytes(Path.Combine(root, "daily.json"));
            settings.DisabledModules.Clear();
            Assert.Equal(0, await workflow.RunTimedAsync(BuiltInModules.AdmittedSchedules(settings), new DateTime(2026, 10, 26, 8, 0, 0),
                (_, _) => throw new Exception(), (_, _) => throw new Exception()));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "daily.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Existing_settings_enable_every_module_and_preferences_round_trip()
    {
        var settings = JsonSerializer.Deserialize<HostSettings>("{}")!;
        Assert.All(BuiltInModules.Available, module => Assert.True(BuiltInModules.Enabled(settings, module.Id)));
        settings.DisabledModules = [BuiltInModules.StudentWork, "future.module"];
        var restored = JsonSerializer.Deserialize<HostSettings>(JsonSerializer.Serialize(settings))!;
        Assert.False(BuiltInModules.Enabled(restored, BuiltInModules.StudentWork));
        Assert.True(BuiltInModules.Enabled(restored, BuiltInModules.Automation));
        Assert.Contains("future.module", restored.DisabledModules);
        restored.DisabledModules = null!;
        Assert.True(BuiltInModules.Enabled(restored, BuiltInModules.StudentWork));
    }

    [Fact]
    public async Task Disabled_sweeps_do_not_claim_and_reenable_preserves_catchup()
    {
        var root = Directory.CreateTempSubdirectory("module-admission-").FullName;
        try
        {
            var settings = new HostSettings { DisabledModules = [BuiltInModules.StudentWork], Schedules =
                [new() { Id = "backup", Action = "backup" }, new() { Id = "lock", Action = "lock" }] };
            Assert.Equal("lock", Assert.Single(BuiltInModules.AdmittedSchedules(settings)).Id);
            settings.DisabledModules.Add(BuiltInModules.Automation);
            var workflow = new ScheduleWorkflow(Path.Combine(root, "daily.json"), Path.Combine(root, "computers.json"));
            Assert.Equal(0, await workflow.RunTimedAsync(BuiltInModules.AdmittedSchedules(settings), DateTime.Now,
                (_, _) => throw new Exception(), (_, _) => throw new Exception()));
            Assert.Empty(Directory.GetFiles(root));
            Assert.Equal(2, settings.Schedules.Count);
            settings.DisabledModules.Clear();
            Assert.Equal(2, BuiltInModules.AdmittedSchedules(settings).Count());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Disabled_backup_refuses_before_touching_a_session_or_checkpoint()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StudentBackup.RunAsync(null!, new(),
            new HostSettings { DisabledModules = [BuiltInModules.StudentWork] }));
        Assert.Equal(BuiltInModules.Disabled, error.Message);
    }

    [Fact]
    public void Module_IDs_match_the_extracted_contracts()
    {
        Assert.Equal(ClassroomManager.Reporting.InventoryReportingModule.Id, BuiltInModules.Reporting);
        Assert.Equal(ClassroomManager.BrowserInventory.BrowserInventoryModule.Id, BuiltInModules.Browser);
        Assert.Equal(ClassroomManager.Automation.SchedulingModule.Id, BuiltInModules.Automation);
        Assert.Equal(ClassroomManager.StudentWork.StudentBackupModule.Id, BuiltInModules.StudentWork);
        Assert.Equal(ClassroomManager.Appearance.AppearanceInventoryModule.Id, BuiltInModules.Appearance);
        Assert.Equal(ClassroomManager.SoftwareInventory.SoftwareInventoryModule.Id, BuiltInModules.Software);
        Assert.Equal(ClassroomManager.Maintenance.MaintenanceInventoryModule.Id, BuiltInModules.Maintenance);
    }

    [Fact]
    public void Appearance_is_a_core_module_that_is_in_every_build_and_can_be_switched_off_like_the_others()
    {
        Assert.Contains(BuiltInModules.Appearance, BuiltInModules.Core);
        // In every build: the build switches name the two optional modules and no core module.
        var props = File.ReadAllText(Path.Combine(StringExtractor.RepositoryRoot(), "ClassroomModules.props"));
        Assert.DoesNotContain("EnableAppearance", props);
        var module = BuiltInModules.Registry.Find(BuiltInModules.Appearance);
        Assert.NotNull(module);
        Assert.NotEmpty(module!.Descriptor!.Icon);   // Host settings draw it beside the tick box, like the other modules

        // For the person it is a module like the others.
        Assert.True(BuiltInModules.Enabled(new HostSettings { DisabledModules = [] }, BuiltInModules.Appearance));
        Assert.False(BuiltInModules.Enabled(new HostSettings { DisabledModules = [BuiltInModules.Appearance] }, BuiltInModules.Appearance));
        Assert.False(BuiltInModules.Enabled(new HostSettings { DisabledModules = [] }, "no.such.module"));
    }

}
